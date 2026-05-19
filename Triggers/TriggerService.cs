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
        if (!_options.Enabled)
        {
            _log.LogInformation("TriggerService disabled (Triggers:Enabled=false)");
            return;
        }
        if (_options.Sources.Count == 0)
        {
            _log.LogWarning("TriggerService enabled but no Triggers:Sources configured — nothing to poll");
            return;
        }

        await _state.InitializeAsync(stoppingToken).ConfigureAwait(false);
        _log.LogInformation("TriggerService starting — {Count} source(s), phrase=\"{Phrase}\", interval={Interval}s",
            _options.Sources.Count, _options.Phrase, _options.PollIntervalSeconds);

        // Give the MCP host a moment to finish connecting on first cycle.
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAllAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Trigger poll cycle failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken).ConfigureAwait(false);
            }
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

    private async Task<int> PollSourceAsync(TriggerSource source, int remainingBudget, CancellationToken ct)
    {
        if (!_mcpHost.Clients.TryGetValue(source.McpServer, out var client))
        {
            _log.LogWarning("Trigger source {Source}: configured MCP server '{Server}' not connected — skipping",
                source.Id, source.McpServer);
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
        var args = new Dictionary<string, object?>
        {
            ["mention"] = EffectivePhrase(source),
            ["sinceUtc"] = since?.ToString("O"),
            ["includeClosed"] = false,
            ["limit"] = Math.Min(budget * 4, 200),
        };
        AddScopeArg(args, source);

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
            sb.Append(" AND [System.ChangedDate] >= '").Append(s.UtcDateTime.ToString("O")).Append('\'');
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
        var agentOpts = scope.ServiceProvider.GetRequiredService<IOptions<AgentOptions>>().Value;

        var model = openAi.DefaultModel;
        var state = agent.CreateState(model);

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
        _ = Task.Run(async () =>
        {
            try { await agent.RunTurnAsync(state, prompt, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogError(ex, "Triggered job {JobId} failed", jobId); }
        }, CancellationToken.None);

        return jobId;
    }
}
