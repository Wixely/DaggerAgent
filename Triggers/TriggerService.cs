using Daggeragent.Agent;
using Daggeragent.Configuration;
using Daggeragent.Mcp;
using Daggeragent.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daggeragent.Triggers;

/// <summary>
/// Background loop that polls every configured ticket source for "@dagger"-style
/// mentions and spawns an agent job per fresh match. Uses MCP servers as the data
/// plane — calls list_mentions_since directly via McpClient.CallToolAsync (no LLM
/// in this path; deterministic and cheap).
/// </summary>
public sealed class TriggerService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TriggerOptions _options;
    private readonly McpClientHost _mcpHost;
    private readonly TriggerStateStore _state;
    private readonly ILogger<TriggerService> _log;

    public TriggerService(
        IServiceProvider services,
        IOptions<TriggerOptions> options,
        McpClientHost mcpHost,
        TriggerStateStore state,
        ILogger<TriggerService> log)
    {
        _services = services;
        _options = options.Value;
        _mcpHost = mcpHost;
        _state = state;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run the loop unconditionally so that flipping Enabled / adding a Source from the
        // web UI takes effect on the next cycle without restarting the host. Each iteration
        // re-reads the live IOptions snapshot. When disabled we still sleep, just don't poll.
        await _state.InitializeAsync(stoppingToken).ConfigureAwait(false);

        // Give the MCP host a moment to finish connecting on first cycle.
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        // Auto-resume any trigger-originated jobs that were orphaned by the last shutdown.
        // The orphan sweep already ran in Program.cs and flipped Running→Paused; here we
        // only re-launch the subset that came from a TriggerSource, respecting the per-job
        // attempt cap so a poison job stops eating retries.
        try { await ResumeOrphanedTriggerJobsAsync(stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _log.LogError(ex, "Auto-resume sweep failed"); }

        bool? lastActive = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var active = _options.Enabled && _options.Sources.Count > 0;
            if (lastActive != active)
            {
                if (active)
                    _log.LogInformation("TriggerService active — {Count} source(s), phrase=\"{Phrase}\", interval={Interval}s",
                        _options.Sources.Count, _options.Phrase, _options.PollIntervalSeconds);
                else if (!_options.Enabled)
                    _log.LogInformation("TriggerService idle (Triggers:Enabled=false)");
                else
                    _log.LogInformation("TriggerService idle (no Sources configured)");
                lastActive = active;
            }

            if (active)
            {
                try
                {
                    await PollAllAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Trigger poll cycle failed");
                }
            }

            // Bound the sleep so a config edit raising/lowering the interval is noticed
            // within ~30s even when we were sleeping a longer interval at the time.
            var interval = Math.Clamp(_options.PollIntervalSeconds, 5, 3600);
            var sleep = active ? TimeSpan.FromSeconds(interval) : TimeSpan.FromSeconds(Math.Min(30, interval));
            try { await Task.Delay(sleep, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollAllAsync(CancellationToken ct)
    {
        var spawned = 0;
        foreach (var source in _options.Sources)
        {
            if (spawned >= _options.MaxJobsPerCycle) break;
            spawned += await PollSourceAsync(source, _options.MaxJobsPerCycle - spawned, ct).ConfigureAwait(false);
        }
        if (spawned > 0) _log.LogInformation("Trigger cycle: spawned {Count} job(s)", spawned);
    }

    private async Task ResumeOrphanedTriggerJobsAsync(CancellationToken ct)
    {
        if (_options.MaxAutoResumeAttempts <= 0)
        {
            _log.LogDebug("Auto-resume disabled (MaxAutoResumeAttempts={Max})", _options.MaxAutoResumeAttempts);
            return;
        }

        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<Persistence.IJobStore>();

        // Walk the most recent jobs and pick out the orphans that came from a TriggerSource.
        // ListAsync returns by updated_at DESC; default limit is 50 which is plenty — older
        // jobs are unlikely to be worth auto-resuming anyway (a long-stopped service won't
        // have a relevant working state).
        var recent = await store.ListAsync(50, ct).ConfigureAwait(false);
        var resumed = 0;
        var skipped = 0;
        foreach (var rec in recent)
        {
            if (!string.Equals(rec.Status, nameof(JobStatus.Paused), StringComparison.OrdinalIgnoreCase)) continue;
            var state = await store.LoadAsync(rec.Id, ct).ConfigureAwait(false);
            if (state is null || !state.Interrupted || string.IsNullOrEmpty(state.TriggerSourceId)) continue;

            if (state.AutoResumeAttempts >= _options.MaxAutoResumeAttempts)
            {
                _log.LogWarning(
                    "Auto-resume: job {JobId} (source={Source}) hit attempt cap ({Cap}) — leaving paused for manual resume",
                    state.Id, state.TriggerSourceId, _options.MaxAutoResumeAttempts);
                skipped++;
                continue;
            }

            state.AutoResumeAttempts++;
            // Persist the bumped counter before kicking off the turn so a crash partway
            // through the resume still counts against the budget.
            await store.SaveAsync(state, ct).ConfigureAwait(false);

            _log.LogInformation(
                "Auto-resuming orphaned trigger job {JobId} (source={Source}, attempt={Attempt}/{Cap})",
                state.Id, state.TriggerSourceId, state.AutoResumeAttempts, _options.MaxAutoResumeAttempts);
            FireAndForgetResume(state);
            resumed++;
        }
        if (resumed > 0 || skipped > 0)
            _log.LogInformation("Auto-resume sweep complete: resumed={Resumed} skipped={Skipped}", resumed, skipped);
    }

    private void FireAndForgetResume(Agent.ConversationState state)
    {
        // Match SpawnJobAsync's fire-and-forget pattern: we don't await the full agent run
        // here; persistence captures completion. Use a fresh scope per task so the agent's
        // scoped dependencies (LlmAgent is transient) don't outlive a single turn.
        var jobId = state.Id;
        var sourceId = state.TriggerSourceId;
        var historyBefore = state.History.Count;
        _ = Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var scope = _services.CreateScope();
                var agent = scope.ServiceProvider.GetRequiredService<Agent.LlmAgent>();
                var response = await agent.ResumeAsync(state, CancellationToken.None).ConfigureAwait(false);
                sw.Stop();

                // Background turns have no SSE consumer, so without an explicit completion log
                // the only sign anything happened is the SQLite row being mutated. Surface the
                // shape of what the agent produced so a missing response (empty / tool-call-only)
                // is visible in the log rather than just in the persisted history blob.
                var newMessages = state.History.Count - historyBefore;
                var assistantText = response?.Text ?? "";
                _log.LogInformation(
                    "Auto-resume completed: job={JobId} source={Source} status={Status} wallMs={WallMs} historyDelta={Delta} assistantChars={AsstChars} assistantSnippet={Snippet}",
                    jobId, sourceId ?? "(none)", state.Status, sw.ElapsedMilliseconds, newMessages,
                    assistantText.Length, Truncate(assistantText, 240));

                if (string.IsNullOrWhiteSpace(assistantText))
                {
                    // A turn that ends without an assistant-visible reply (e.g. only tool calls,
                    // or the model returned an empty answer) is the failure mode the user hit:
                    // the chat looks dead but nothing crashed. Promote it so it's spotted quickly.
                    _log.LogWarning(
                        "Auto-resume produced no assistant text for job {JobId} (historyDelta={Delta}, finishReason={Finish}). " +
                        "Check the persisted history — the agent may have stalled mid-tool-loop or hit max turns.",
                        jobId, newMessages, state.LastTurnFinishReason ?? "(none)");
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                _log.LogError(ex, "Auto-resumed job {JobId} failed after {WallMs}ms", jobId, sw.ElapsedMilliseconds);
            }
        }, CancellationToken.None);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Manually run a single trigger source out-of-band — used by the web UI "Run now" button.
    /// Bypasses the background loop's interval but otherwise behaves identically: same MCP server
    /// lookup, same per-match dedupe via <see cref="TriggerStateStore.ClaimMatchAsync"/>, and the
    /// cursor advances on completion. Returns a small report so the UI can show the outcome.
    /// </summary>
    public async Task<RunSourceOnceResult> RunSourceOnceAsync(string sourceId, CancellationToken ct)
    {
        var source = _options.Sources.FirstOrDefault(s =>
            string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        if (source is null)
            return new RunSourceOnceResult(false, 0, $"No trigger source with id '{sourceId}'");

        try
        {
            var budget = Math.Max(1, _options.MaxJobsPerCycle);
            var spawned = await PollSourceAsync(source, budget, ct).ConfigureAwait(false);
            return new RunSourceOnceResult(true, spawned, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Manual run of trigger source {Source} failed", sourceId);
            return new RunSourceOnceResult(false, 0, ex.Message);
        }
    }

    private async Task<int> PollSourceAsync(TriggerSource source, int remainingBudget, CancellationToken ct)
    {
        if (!_mcpHost.Clients.TryGetValue(source.McpServer, out var client))
        {
            // Surface enough context to tell apart the two common failure modes: the name in
            // the trigger config doesn't match any configured Mcp:Servers entry, or it matches
            // but the connection itself failed. The status dictionary holds the connect-time error.
            var status = _mcpHost.ConnectionStatuses.FirstOrDefault(s =>
                string.Equals(s.Name, source.McpServer, StringComparison.OrdinalIgnoreCase));
            if (status is null)
            {
                var known = string.Join(", ", _mcpHost.ConnectionStatuses.Select(s => s.Name));
                _log.LogWarning(
                    "Trigger source {Source}: MCP server '{Server}' is not configured. Known servers: [{Known}]",
                    source.Id, source.McpServer, known);
            }
            else
            {
                _log.LogWarning(
                    "Trigger source {Source}: MCP server '{Server}' has status={Status} ({Detail}) — skipping",
                    source.Id, source.McpServer, status.Status, status.Detail ?? "no detail");
            }
            return 0;
        }

        var since = await _state.GetLastPolledAsync(source.Id, ct).ConfigureAwait(false);
        var pollStart = DateTimeOffset.UtcNow;

        IReadOnlyList<TriggerMatch> matches;
        try
        {
            matches = source.Mode switch
            {
                TriggerMode.Mentions => await FetchMentionsAsync(client, source, since, remainingBudget, ct).ConfigureAwait(false),
                TriggerMode.Label    => await FetchIssuesAsync(client, source, since, remainingBudget, ct).ConfigureAwait(false),
                TriggerMode.Assignee => await FetchIssuesAsync(client, source, since, remainingBudget, ct).ConfigureAwait(false),
                TriggerMode.AllNew   => await FetchIssuesAsync(client, source, since, remainingBudget, ct).ConfigureAwait(false),
                _                    => Array.Empty<TriggerMatch>(),
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Trigger source {Source}: poll ({Mode}) failed", source.Id, source.Mode);
            return 0;
        }

        // Advance our cursor even on empty results — otherwise we'd keep re-fetching from epoch.
        await _state.SetLastPolledAsync(source.Id, pollStart, ct).ConfigureAwait(false);

        if (matches.Count == 0) return 0;

        // Author allowlist (empty list = allow anyone).
        var allowed = _options.AllowedAuthors.Count == 0
            ? (Func<string, bool>)(_ => true)
            : a => _options.AllowedAuthors.Any(x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase));

        // For Mentions mode, also re-check the phrase server-side-filtered already, but cheap defence-in-depth.
        var phraseCheck = source.Mode == TriggerMode.Mentions
            ? EffectivePhrase(source)
            : null;

        var spawned = 0;
        foreach (var match in matches.OrderBy(m => m.UpdatedAt))
        {
            if (spawned >= remainingBudget) break;
            if (!allowed(match.Author)) continue;
            if (phraseCheck is not null && !match.Body.Contains(phraseCheck, StringComparison.OrdinalIgnoreCase)) continue;

            var key = match.MatchKey();
            if (!await _state.ClaimMatchAsync(source.Id, key, ct).ConfigureAwait(false))
            {
                continue;  // already seen
            }

            try
            {
                var jobId = await SpawnJobAsync(source, match, ct).ConfigureAwait(false);
                await _state.AttachJobIdAsync(source.Id, key, jobId, ct).ConfigureAwait(false);
                spawned++;
                _log.LogInformation("Triggered job {JobId} for {Source}:{Ref} (mode={Mode}, author={Author})",
                    jobId, source.Id, match.ShortRef(), source.Mode, match.Author);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to spawn job for {Source}:{Ref}", source.Id, match.ShortRef());
            }
        }
        return spawned;
    }

    private string EffectivePhrase(TriggerSource source) =>
        string.IsNullOrWhiteSpace(source.Filter) ? _options.Phrase : source.Filter;

    private async Task<IReadOnlyList<TriggerMatch>> FetchMentionsAsync(
        ModelContextProtocol.Client.McpClient client, TriggerSource source, DateTimeOffset? since, int budget, CancellationToken ct)
    {
        // Azure DevOps' WIQL treats date fields as date precision and rejects any time-bearing
        // literal. The azdo MCP server's list_mentions_since builds WIQL from this string, so
        // for that kind we hand it a date-only value. Per-match dedupe in TriggerStateStore
        // absorbs the resulting same-day re-polls.
        var kind = source.Kind.Trim().ToLowerInvariant();
        var isAzdo = kind is "azuredevops" or "azdo";
        var sinceWire = since is { } s
            ? (isAzdo ? s.UtcDateTime.ToString("yyyy-MM-dd") : s.ToString("O"))
            : null;

        var args = new Dictionary<string, object?>
        {
            ["mention"] = EffectivePhrase(source),
            ["sinceUtc"] = sinceWire,
            ["includeClosed"] = false,
            ["limit"] = Math.Min(budget * 4, 200),
        };
        AddScopeArg(args, source);

        _log.LogDebug("Trigger source {Source}: calling list_mentions_since with sinceUtc={Since} kind={Kind}",
            source.Id, sinceWire ?? "(null)", kind);

        var envelope = await McpStructuredCall.CallAsync<TriggerMatchEnvelope>(client, "list_mentions_since", args, ct).ConfigureAwait(false);
        if (envelope is null) return Array.Empty<TriggerMatch>();
        if (!string.IsNullOrEmpty(envelope.Error))
        {
            _log.LogWarning("Trigger source {Source}: server error: {Error}", source.Id, envelope.Error);
            return Array.Empty<TriggerMatch>();
        }
        return envelope.Matches;
    }

    private async Task<IReadOnlyList<TriggerMatch>> FetchIssuesAsync(
        ModelContextProtocol.Client.McpClient client, TriggerSource source, DateTimeOffset? since, int budget, CancellationToken ct)
    {
        var kind = source.Kind.Trim().ToLowerInvariant();
        return kind switch
        {
            "github"     => await FetchGitHubIssuesAsync(client, source, since, budget, ct).ConfigureAwait(false),
            "gitlab"     => await FetchGitLabIssuesAsync(client, source, since, budget, ct).ConfigureAwait(false),
            "azuredevops" or "azdo"
                         => await FetchAzdoWorkItemsAsync(client, source, since, budget, ct).ConfigureAwait(false),
            _ => Array.Empty<TriggerMatch>(),
        };
    }

    private async Task<IReadOnlyList<TriggerMatch>> FetchGitHubIssuesAsync(
        ModelContextProtocol.Client.McpClient client, TriggerSource source, DateTimeOffset? since, int budget, CancellationToken ct)
    {
        var args = new Dictionary<string, object?>
        {
            ["state"] = "open",
            ["updatedSinceUtc"] = since?.ToString("O"),
        };
        AddScopeArg(args, source);
        if (source.Mode == TriggerMode.Label && !string.IsNullOrWhiteSpace(source.Filter))
            args["labels"] = source.Filter;
        if (source.Mode == TriggerMode.Assignee && !string.IsNullOrWhiteSpace(source.Filter))
            args["assignee"] = source.Filter;

        var issues = await McpStructuredCall.CallAsync<List<GitHubIssueListItem>>(client, "list_issues", args, ct).ConfigureAwait(false);
        if (issues is null) return Array.Empty<TriggerMatch>();
        var slug = source.Scope;
        return issues.Select(i => new TriggerMatch
        {
            Kind = "issue",
            Repo = slug,
            Number = i.Number,
            Author = i.User ?? "",
            Body = i.Title ?? "",
            Url = i.HtmlUrl ?? "",
            CreatedAt = i.CreatedAt,
            UpdatedAt = i.UpdatedAt,
        }).Take(budget).ToList();
    }

    private async Task<IReadOnlyList<TriggerMatch>> FetchGitLabIssuesAsync(
        ModelContextProtocol.Client.McpClient client, TriggerSource source, DateTimeOffset? since, int budget, CancellationToken ct)
    {
        var args = new Dictionary<string, object?>
        {
            ["state"] = "opened",
            ["updatedSinceUtc"] = since?.ToString("O"),
        };
        AddScopeArg(args, source);
        if (source.Mode == TriggerMode.Label && !string.IsNullOrWhiteSpace(source.Filter))
            args["labels"] = source.Filter;
        if (source.Mode == TriggerMode.Assignee && !string.IsNullOrWhiteSpace(source.Filter))
            args["assigneeUsername"] = source.Filter;

        var issues = await McpStructuredCall.CallAsync<List<GitLabIssueListItem>>(client, "list_issues", args, ct).ConfigureAwait(false);
        if (issues is null) return Array.Empty<TriggerMatch>();
        return issues.Select(i => new TriggerMatch
        {
            Kind = "issue",
            Project = source.Scope,
            Iid = i.Iid,
            Author = i.Author ?? "",
            Body = i.Title ?? "",
            Url = i.WebUrl ?? "",
            CreatedAt = i.CreatedAt,
            UpdatedAt = i.UpdatedAt,
        }).Take(budget).ToList();
    }

    private async Task<IReadOnlyList<TriggerMatch>> FetchAzdoWorkItemsAsync(
        ModelContextProtocol.Client.McpClient client, TriggerSource source, DateTimeOffset? since, int budget, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("SELECT [System.Id] FROM WorkItems WHERE ");
        if (!string.IsNullOrWhiteSpace(source.Scope))
            sb.Append("[System.TeamProject] = '").Append(EscapeWiql(source.Scope)).Append("' AND ");
        sb.Append("[System.State] NOT IN ('Closed', 'Resolved', 'Done', 'Removed')");
        if (since is { } s)
        {
            // WIQL treats [System.ChangedDate] as date precision — supplying a time component
            // returns "You cannot supply a time with the date when running a query using date
            // precision." Format as date-only; the per-match dedupe in TriggerStateStore
            // (INSERT OR IGNORE on source_id+match_key) absorbs the resulting same-day re-polls.
            sb.Append(" AND [System.ChangedDate] >= '").Append(s.UtcDateTime.ToString("yyyy-MM-dd")).Append('\'');
        }
        if (source.Mode == TriggerMode.Label && !string.IsNullOrWhiteSpace(source.Filter))
            sb.Append(" AND [System.Tags] CONTAINS '").Append(EscapeWiql(source.Filter)).Append('\'');
        if (source.Mode == TriggerMode.Assignee && !string.IsNullOrWhiteSpace(source.Filter))
            sb.Append(" AND [System.AssignedTo] = '").Append(EscapeWiql(source.Filter)).Append('\'');
        sb.Append(" ORDER BY [System.ChangedDate] DESC");

        var args = new Dictionary<string, object?>
        {
            ["wiql"] = sb.ToString(),
            ["top"] = Math.Min(budget * 4, 200),
        };
        if (!string.IsNullOrWhiteSpace(source.Scope)) args["project"] = source.Scope;

        var items = await McpStructuredCall.CallAsync<List<AzdoWorkItem>>(client, "query_work_items", args, ct).ConfigureAwait(false);
        if (items is null) return Array.Empty<TriggerMatch>();
        return items.Select(w => new TriggerMatch
        {
            Kind = "work_item",
            Project = source.Scope,
            Id = w.Id,
            WorkItemType = TryGetField(w.Fields, "System.WorkItemType"),
            Title = TryGetField(w.Fields, "System.Title"),
            Author = TryGetField(w.Fields, "System.CreatedBy") ?? "",
            Body = TryGetField(w.Fields, "System.Title") ?? "",
            Url = w.Url ?? "",
            CreatedAt = TryGetFieldDate(w.Fields, "System.CreatedDate") ?? default,
            UpdatedAt = TryGetFieldDate(w.Fields, "System.ChangedDate") ?? default,
        }).Take(budget).ToList();
    }

    private static string EscapeWiql(string s) => s.Replace("'", "''");

    private static string? TryGetField(Dictionary<string, System.Text.Json.JsonElement>? fields, string key)
    {
        if (fields is null) return null;
        if (!fields.TryGetValue(key, out var v)) return null;
        if (v.ValueKind == System.Text.Json.JsonValueKind.String) return v.GetString();
        // AzDO sometimes returns user fields as { displayName, uniqueName, ... }
        if (v.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (v.TryGetProperty("uniqueName", out var u)) return u.GetString();
            if (v.TryGetProperty("displayName", out var d)) return d.GetString();
        }
        return v.ToString();
    }

    private static DateTimeOffset? TryGetFieldDate(Dictionary<string, System.Text.Json.JsonElement>? fields, string key)
    {
        var s = TryGetField(fields, key);
        return DateTimeOffset.TryParse(s, out var dt) ? dt : null;
    }

    private static void AddScopeArg(Dictionary<string, object?> args, TriggerSource source)
    {
        if (string.IsNullOrWhiteSpace(source.Scope)) return;
        switch (source.Kind.Trim().ToLowerInvariant())
        {
            case "github":
                var slash = source.Scope.IndexOf('/');
                if (slash > 0)
                {
                    args["owner"] = source.Scope[..slash];
                    args["repo"] = source.Scope[(slash + 1)..];
                }
                break;
            case "gitlab":
                args["project"] = source.Scope;
                break;
            case "azuredevops":
            case "azdo":
                args["project"] = source.Scope;
                break;
        }
    }

    // Per-provider list_issues / query_work_items response DTOs.
    // Property names match the MCP servers' JsonOpts.Default output (PascalCase).
    private sealed class GitHubIssueListItem
    {
        public int Number { get; set; }
        public string? Title { get; set; }
        public string? User { get; set; }
        public string? HtmlUrl { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class GitLabIssueListItem
    {
        public int Iid { get; set; }
        public string? Title { get; set; }
        public string? Author { get; set; }
        public string? WebUrl { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class AzdoWorkItem
    {
        public int Id { get; set; }
        public string? Url { get; set; }
        public Dictionary<string, System.Text.Json.JsonElement>? Fields { get; set; }
    }

    private async Task<string> SpawnJobAsync(TriggerSource source, TriggerMatch match, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var agent = scope.ServiceProvider.GetRequiredService<LlmAgent>();
        var openAi = scope.ServiceProvider.GetRequiredService<IOptions<OpenAIOptions>>().Value;
        var endpoints = scope.ServiceProvider.GetRequiredService<IOptions<EndpointsOptions>>().Value;
        var agentOpts = scope.ServiceProvider.GetRequiredService<IOptions<AgentOptions>>().Value;

        // Resolve per-source endpoint override → chosen endpoint's default model →
        // legacy OpenAI section. The model used to seed state.Model is whatever the
        // resolved endpoint considers its default; explicit source.Model wins over that.
        EndpointConfig? chosenEndpoint = null;
        if (!string.IsNullOrWhiteSpace(source.EndpointId))
        {
            chosenEndpoint = endpoints.Items.FirstOrDefault(e =>
                string.Equals(e.Id, source.EndpointId, StringComparison.OrdinalIgnoreCase));
            if (chosenEndpoint is null)
            {
                _log.LogWarning(
                    "Trigger source {Source}: EndpointId='{EndpointId}' not found — falling back to global default",
                    source.Id, source.EndpointId);
            }
        }

        var model = !string.IsNullOrWhiteSpace(source.Model)
            ? source.Model
            : (!string.IsNullOrWhiteSpace(chosenEndpoint?.DefaultModel)
                ? chosenEndpoint!.DefaultModel
                : openAi.DefaultModel);

        var state = agent.CreateState(model);
        if (chosenEndpoint is not null) state.EndpointId = chosenEndpoint.Id;
        // Stamp the trigger source so the startup auto-resume sweep knows which orphans were
        // ours to relaunch (versus user-initiated jobs which stay paused for a manual click).
        state.TriggerSourceId = source.Id;

        var prompt =
            $"{_options.JobPreamble}\n\n" +
            $"Source:    {source.Id} ({source.Kind})\n" +
            $"Reference: {match.ShortRef()}\n" +
            $"Author:    {match.Author}\n" +
            $"URL:       {match.Url}\n" +
            $"Updated:   {match.UpdatedAt:u}\n" +
            $"\n--- body ---\n{match.Body}\n";

        // Fire and forget — TriggerService doesn't await the agent's full execution; we just
        // start it and return. The agent's persistence layer captures completion / failure.
        var jobId = state.Id;
        var sourceId = source.Id;
        _ = Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var response = await agent.RunTurnAsync(state, prompt, CancellationToken.None).ConfigureAwait(false);
                sw.Stop();
                // Background turn has no SSE consumer; emit a completion line so a "nothing
                // happened" run is visible. assistantSnippet helps tell apart a real reply from
                // a tool-call-only / empty-result turn that leaves the chat looking dead.
                var assistantText = response?.Text ?? "";
                _log.LogInformation(
                    "Triggered job completed: job={JobId} source={Source} status={Status} wallMs={WallMs} assistantChars={AsstChars} assistantSnippet={Snippet}",
                    jobId, sourceId, state.Status, sw.ElapsedMilliseconds, assistantText.Length, Truncate(assistantText, 240));
                if (string.IsNullOrWhiteSpace(assistantText))
                {
                    _log.LogWarning(
                        "Triggered job {JobId} produced no assistant text (finishReason={Finish}). " +
                        "The chat will look empty in the UI; inspect history for stuck tool loops or empty model output.",
                        jobId, state.LastTurnFinishReason ?? "(none)");
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                _log.LogError(ex, "Triggered job {JobId} failed after {WallMs}ms", jobId, sw.ElapsedMilliseconds);
            }
        }, CancellationToken.None);

        return jobId;
    }
}

/// <summary>Outcome of <see cref="TriggerService.RunSourceOnceAsync"/>.</summary>
public sealed record RunSourceOnceResult(bool Ok, int Spawned, string? Error);
