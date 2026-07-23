using System.Text.Json;
using Daggeragent.Configuration;
using Daggeragent.Mcp;
using Daggeragent.Persistence;
using Daggeragent.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Daggeragent.Server;

/// <summary>
/// HTML/JS/CSS shell for the embedded Web UI plus the read/write endpoints it depends on:
/// MCP server list, tool catalogue, slash-command catalogue, settings (runtime mutation
/// of <see cref="ToolsOptions"/>), per-job plan, pending writes (approve/discard).
///
/// All UI assets ship as <c>EmbeddedResource</c> entries in DaggerAgent.csproj
/// (Server\Ui\**) and are served via <see cref="EmbeddedAssets.TryGetUiAsset"/>.
/// </summary>
public static class AgentUiEndpoints
{
    public static IEndpointRouteBuilder MapAgentUi(this IEndpointRouteBuilder app, string basePath = "/agent")
    {
        var group = app.MapGroup(basePath);

        // ──────────────────────────── static assets ────────────────────────────

        // Two routes, both serve the HTML shell so trailing-slash doesn't matter; sub-paths
        // dispatch to embedded assets. The HTML uses absolute hrefs ("__BASE__/halfmoon.min.css")
        // that we rewrite at serve time, so the basePath being configurable still works.
        var uiPrefix = $"{basePath}/ui";
        group.MapGet("/ui", () => ServeIndexHtml(uiPrefix));
        group.MapGet("/ui/{**path}", (string? path) =>
            string.IsNullOrEmpty(path) ? ServeIndexHtml(uiPrefix) : ServeAsset(path));

        // ──────────────────────────── endpoints (LLM) ────────────────────────────

        // Returns the configured endpoints + which one is currently active by default.
        group.MapGet("/endpoints", (IOptions<EndpointsOptions> endpoints) =>
        {
            var v = endpoints.Value;
            return Results.Json(new
            {
                defaultId = v.DefaultId,
                items = v.Items.Select(ToEndpointView).ToList(),
            }, JsonOpts.Default);
        });

        // Create or update by Id. API-key field is masked on GET but accepted as-typed on POST.
        // Sending an empty string clears the key; omitting the field keeps the existing value.
        group.MapPost("/endpoints", async (
            EndpointPatch patch,
            IOptions<EndpointsOptions> endpoints,
            RuntimeConfigStore store,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(patch.Id))
                return Results.BadRequest(new { error = "id required" });

            var v = endpoints.Value;
            var existing = v.Items.FirstOrDefault(e => string.Equals(e.Id, patch.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new EndpointConfig { Id = patch.Id };
                v.Items.Add(existing);
            }
            if (patch.DisplayName is not null) existing.DisplayName = patch.DisplayName;
            if (patch.Provider is not null) existing.Provider = patch.Provider;
            if (patch.BaseUrl is not null) existing.BaseUrl = patch.BaseUrl;
            if (patch.ApiKey is not null) existing.ApiKey = patch.ApiKey;
            if (patch.DefaultModel is not null) existing.DefaultModel = patch.DefaultModel;
            if (patch.RequestTimeoutSeconds is int t) existing.RequestTimeoutSeconds = t;
            if (patch.Enabled is bool en) existing.Enabled = en;
            if (patch.MaxContextTokens is int mct) existing.MaxContextTokens = mct;
            if (patch.MaxOutputTokens is int mot) existing.MaxOutputTokens = mot;
            if (patch.ClaudePermissionMode is not null) existing.ClaudePermissionMode = patch.ClaudePermissionMode;
            if (patch.ClaudeAllowedTools is not null)
            {
                existing.ClaudeAllowedTools.Clear();
                foreach (var s in patch.ClaudeAllowedTools.Where(x => !string.IsNullOrWhiteSpace(x)))
                    existing.ClaudeAllowedTools.Add(s.Trim());
            }
            if (patch.ClaudeDangerouslySkipPermissions is bool skip) existing.ClaudeDangerouslySkipPermissions = skip;
            if (patch.CodexSandbox is not null) existing.CodexSandbox = patch.CodexSandbox;
            if (patch.CodexAskForApproval is not null) existing.CodexAskForApproval = patch.CodexAskForApproval;
            if (patch.CopilotAllowAllTools is bool cpAllowAll) existing.CopilotAllowAllTools = cpAllowAll;
            if (patch.CopilotAllowAllPaths is bool cpAllowPaths) existing.CopilotAllowAllPaths = cpAllowPaths;
            if (patch.CopilotAllowAllUrls is bool cpAllowUrls) existing.CopilotAllowAllUrls = cpAllowUrls;
            if (patch.CopilotAutopilot is bool cpAuto) existing.CopilotAutopilot = cpAuto;
            if (patch.CopilotMaxAutopilotContinues is int cpMax) existing.CopilotMaxAutopilotContinues = cpMax;
            if (patch.CopilotNoAskUser is bool cpNoAsk) existing.CopilotNoAskUser = cpNoAsk;
            if (patch.CopilotAllowedTools is not null)
            {
                existing.CopilotAllowedTools.Clear();
                foreach (var s in patch.CopilotAllowedTools.Where(x => !string.IsNullOrWhiteSpace(x)))
                    existing.CopilotAllowedTools.Add(s.Trim());
            }
            if (patch.CopilotDeniedTools is not null)
            {
                existing.CopilotDeniedTools.Clear();
                foreach (var s in patch.CopilotDeniedTools.Where(x => !string.IsNullOrWhiteSpace(x)))
                    existing.CopilotDeniedTools.Add(s.Trim());
            }

            await store.SaveAsync(ct).ConfigureAwait(false);
            return Results.Json(ToEndpointView(existing), JsonOpts.Default);
        });

        group.MapDelete("/endpoints/{id}", async (
            string id,
            IOptions<EndpointsOptions> endpoints,
            RuntimeConfigStore store,
            CancellationToken ct) =>
        {
            var v = endpoints.Value;
            var removed = v.Items.RemoveAll(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return Results.NotFound();
            if (string.Equals(v.DefaultId, id, StringComparison.OrdinalIgnoreCase)) v.DefaultId = null;
            await store.SaveAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        // Set the global "active" endpoint that new jobs inherit.
        group.MapPost("/endpoints/{id}/activate", async (
            string id,
            IOptions<EndpointsOptions> endpoints,
            RuntimeConfigStore store,
            CancellationToken ct) =>
        {
            var v = endpoints.Value;
            var match = v.Items.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (match is null) return Results.NotFound();
            v.DefaultId = match.Id;
            await store.SaveAsync(ct).ConfigureAwait(false);
            return Results.Json(new { defaultId = v.DefaultId }, JsonOpts.Default);
        });

        // ──────────────────────────── MCP servers (config CRUD) ────────────────────────────

        // Note: /mcp/servers (further down) returns runtime connection STATUS — useful for the
        // sidebar. This pair returns the CONFIG list with all editable fields so the UI can
        // surface add/edit/delete + the per-server PassthroughToCli flag.

        group.MapGet("/mcp-config", (IOptions<McpOptions> mcp) =>
        {
            return Results.Json(mcp.Value.Servers.Select(ToMcpServerView).ToList(), JsonOpts.Default);
        });

        group.MapPost("/mcp-config", async (
            McpServerPatch patch,
            IOptions<McpOptions> mcp,
            RuntimeConfigStore store,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(patch.Name))
                return Results.BadRequest(new { error = "name required" });
            var v = mcp.Value;
            var existing = v.Servers.FirstOrDefault(s => string.Equals(s.Name, patch.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new McpServerConfig { Name = patch.Name };
                v.Servers.Add(existing);
            }
            if (patch.Enabled is bool en) existing.Enabled = en;
            if (patch.Url is not null) existing.Url = patch.Url;
            if (patch.AuthHeader is not null) existing.AuthHeader = patch.AuthHeader;
            if (patch.Command is not null) existing.Command = patch.Command;
            if (patch.Arguments is not null) existing.Arguments = patch.Arguments.ToList();
            if (patch.WorkingDirectory is not null) existing.WorkingDirectory = patch.WorkingDirectory;
            if (patch.EnvironmentVariables is not null) existing.EnvironmentVariables = new Dictionary<string, string>(patch.EnvironmentVariables);
            if (patch.PassthroughToCli is bool pt) existing.PassthroughToCli = pt;
            await store.SaveAsync(ct).ConfigureAwait(false);
            return Results.Json(ToMcpServerView(existing), JsonOpts.Default);
        });

        group.MapDelete("/mcp-config/{name}", async (
            string name,
            IOptions<McpOptions> mcp,
            RuntimeConfigStore store,
            CancellationToken ct) =>
        {
            var v = mcp.Value;
            var removed = v.Servers.RemoveAll(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return Results.NotFound();
            await store.SaveAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        // ──────────────────────────── MCP ────────────────────────────

        group.MapGet("/mcp/servers", (McpClientHost host) =>
        {
            var statuses = host.ConnectionStatuses;
            var clients = host.Clients;
            var views = new List<McpServerView>(statuses.Count);
            foreach (var s in statuses)
            {
                var toolViews = new List<McpToolView>();
                if (clients.TryGetValue(s.Name, out _))
                {
                    foreach (var t in host.AllTools)
                    {
                        if (t is AIFunction af &&
                            (af.Name.StartsWith($"mcp.{s.Name}.", StringComparison.OrdinalIgnoreCase) ||
                             af.Name.StartsWith($"{s.Name}.", StringComparison.OrdinalIgnoreCase)))
                        {
                            toolViews.Add(new McpToolView(af.Name, af.Description));
                        }
                    }
                }
                views.Add(new McpServerView(s.Name, s.Status, s.Transport, s.ToolCount, s.Detail, toolViews));
            }
            return Results.Json(views, JsonOpts.Default);
        });

        group.MapPost("/mcp/reload", async (McpClientHost host, CancellationToken ct) =>
        {
            await host.ReloadAsync(ct).ConfigureAwait(false);
            return Results.Json(new { reloaded = true, servers = host.ConnectionStatuses }, JsonOpts.Default);
        });

        // ──────────────────────────── triggers ────────────────────────────

        // Single GET returns the full trigger options plus the MCP server names so the UI's
        // McpServer dropdown can list valid choices. EndpointId dropdown is populated from the
        // already-existing /endpoints response on the client.
        group.MapGet("/triggers", (
            IOptions<TriggerOptions> triggers,
            IOptions<McpOptions> mcp) =>
        {
            var v = triggers.Value;
            return Results.Json(new
            {
                enabled = v.Enabled,
                pollIntervalSeconds = v.PollIntervalSeconds,
                phrase = v.Phrase,
                allowedAuthors = v.AllowedAuthors,
                maxJobsPerCycle = v.MaxJobsPerCycle,
                jobPreamble = v.JobPreamble,
                maxAutoResumeAttempts = v.MaxAutoResumeAttempts,
                maxConcurrentJobs = v.MaxConcurrentJobs,
                sources = v.Sources.Select(ToTriggerSourceView).ToList(),
                mcpServerNames = mcp.Value.Servers.Where(s => s.Enabled).Select(s => s.Name).ToList(),
            }, JsonOpts.Default);
        });

        // PATCH the top-level options (everything except the Sources list). Each field is
        // optional; only fields present in the body are touched, matching the endpoint/mcp
        // patch shape elsewhere.
        group.MapPost("/triggers", async (
            TriggerOptionsPatch patch,
            IOptions<TriggerOptions> triggers,
            RuntimeConfigStore store,
            CancellationToken ct) =>
        {
            var v = triggers.Value;
            if (patch.Enabled is bool en) v.Enabled = en;
            if (patch.PollIntervalSeconds is int p && p > 0) v.PollIntervalSeconds = p;
            if (patch.Phrase is not null) v.Phrase = patch.Phrase;
            if (patch.MaxJobsPerCycle is int m && m > 0) v.MaxJobsPerCycle = m;
            if (patch.JobPreamble is not null) v.JobPreamble = patch.JobPreamble;
            if (patch.MaxAutoResumeAttempts is int mar && mar >= 0) v.MaxAutoResumeAttempts = mar;
            if (patch.MaxConcurrentJobs is int mcj && mcj >= 1) v.MaxConcurrentJobs = mcj;
            if (patch.AllowedAuthors is not null)
            {
                v.AllowedAuthors.Clear();
                foreach (var a in patch.AllowedAuthors.Where(x => !string.IsNullOrWhiteSpace(x)))
                    v.AllowedAuthors.Add(a.Trim());
            }
            await store.SaveAsync(ct).ConfigureAwait(false);
            return Results.Json(new { ok = true }, JsonOpts.Default);
        });

        // Upsert a single source by Id. Empty Id is rejected. Unknown McpServer is allowed
        // (the user might be configuring before connecting the server) — TriggerService logs
        // a warning at poll time if the named server isn't connected.
        group.MapPost("/triggers/sources", async (
            TriggerSourcePatch patch,
            IOptions<TriggerOptions> triggers,
            RuntimeConfigStore store,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(patch.Id))
                return Results.BadRequest(new { error = "id required" });

            var v = triggers.Value;
            var existing = v.Sources.FirstOrDefault(s => string.Equals(s.Id, patch.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new TriggerSource { Id = patch.Id };
                v.Sources.Add(existing);
            }
            if (patch.Kind is not null) existing.Kind = patch.Kind;
            if (patch.Mode is not null && Enum.TryParse<TriggerMode>(patch.Mode, ignoreCase: true, out var modeVal))
                existing.Mode = modeVal;
            if (patch.Filter is not null) existing.Filter = patch.Filter;
            if (patch.McpServer is not null) existing.McpServer = patch.McpServer;
            if (patch.Scope is not null) existing.Scope = patch.Scope;
            if (patch.EndpointId is not null) existing.EndpointId = patch.EndpointId;
            if (patch.Model is not null) existing.Model = patch.Model;

            await store.SaveAsync(ct).ConfigureAwait(false);
            return Results.Json(ToTriggerSourceView(existing), JsonOpts.Default);
        });

        group.MapDelete("/triggers/sources/{id}", async (
            string id,
            IOptions<TriggerOptions> triggers,
            RuntimeConfigStore store,
            CancellationToken ct) =>
        {
            var v = triggers.Value;
            var removed = v.Sources.RemoveAll(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return Results.NotFound();
            await store.SaveAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        // Manually run a single trigger source out-of-band. Behaves like one iteration of the
        // background loop's per-source poll — same MCP lookup, same per-match dedupe — so calling
        // it on an idle source spawns nothing, and calling it after new activity spawns jobs.
        group.MapPost("/triggers/sources/{id}/run", async (
            string id,
            Daggeragent.Triggers.TriggerService triggers,
            CancellationToken ct) =>
        {
            var result = await triggers.RunSourceOnceAsync(id, ct).ConfigureAwait(false);
            if (!result.Ok && result.Error is { } err && err.StartsWith("No trigger source"))
                return Results.NotFound(new { error = err });
            return Results.Json(new
            {
                ok = result.Ok,
                spawned = result.Spawned,
                error = result.Error,
            }, JsonOpts.Default);
        });

        // ──────────────────────────── tools ────────────────────────────

        group.MapGet("/tools", (
            BuiltInToolRegistry builtIns,
            McpClientHost mcpHost,
            IOptions<ToolsOptions> toolsOpts) =>
        {
            var opts = toolsOpts.Value;
            var items = new List<ToolListItem>();

            foreach (var tool in builtIns.ForAgent(parentJobId: null, currentDepth: 0))
            {
                if (tool is not AIFunction af) continue;
                var category = ToolCategorizer.CategoryFor(af.Name).ToString();
                var (enabled, reason) = ClassifyBuiltInEnablement(af.Name, opts);
                items.Add(new ToolListItem(af.Name, af.Description, category, "built-in", enabled, reason));
            }

            foreach (var tool in mcpHost.AllTools)
            {
                if (tool is not AIFunction af) continue;
                items.Add(new ToolListItem(af.Name, af.Description, ToolCategorizer.CategoryFor(af.Name).ToString(), "mcp", true, null));
            }

            return Results.Json(items, JsonOpts.Default);
        });

        // ──────────────────────────── slash commands ────────────────────────────

        group.MapGet("/commands", () =>
        {
            // Mirrors the InteractiveRunner slash-command set so the UI can render the
            // same command palette the console has.
            var commands = new[]
            {
                new CommandView("/new", "Start a new job (resets the transcript)", null),
                new CommandView("/resume", "Switch to an existing job by id", "/resume <jobId>"),
                new CommandView("/jobs", "Show the recent-jobs picker", null),
                new CommandView("/mcpreload", "Reload all configured MCP servers", null),
                new CommandView("/compress", "Compact the conversation history to free context tokens", null),
                new CommandView("/help", "List the available slash commands", null),
            };
            return Results.Json(commands, JsonOpts.Default);
        });

        // ──────────────────────────── settings ────────────────────────────

        group.MapGet("/settings", (IOptions<ToolsOptions> toolsOpts) =>
        {
            var v = toolsOpts.Value;
            return Results.Json(new ToolsSettingsView(
                v.WorkingDirectory, v.AllowAnyPath, v.ReadOnly, v.AllowWrite, v.WritePreview,
                v.AllowShell, v.MaxFileBytes, v.MaxResults, v.ShellTimeoutSeconds,
                v.GranularTools, v.ForcePlan, v.ReadFileSummaryThresholdBytes, v.MaxToolResultChars,
                v.AllowCliDelegation, v.ClaudeCliPath, v.CodexCliPath, v.CopilotCliPath), JsonOpts.Default);
        });

        // Mutates the singleton IOptions<ToolsOptions> instance in place. Every code path
        // that reads ToolsOptions does so via IOptions<T> (verified by exploration); none
        // use IOptionsMonitor for change tracking, so the mutation is observed everywhere.
        group.MapPost("/settings", async (
            ToolsOptionsPatch patch,
            IOptions<ToolsOptions> toolsOpts,
            RuntimeConfigStore runtimeStore,
            CancellationToken ct) =>
        {
            var v = toolsOpts.Value;
            if (patch.WorkingDirectory is not null && patch.WorkingDirectory != v.WorkingDirectory)
                v.WorkingDirectory = patch.WorkingDirectory;
            if (patch.AllowAnyPath is bool ap) v.AllowAnyPath = ap;
            if (patch.ReadOnly is bool ro) v.ReadOnly = ro;
            if (patch.AllowWrite is bool aw) v.AllowWrite = aw;
            if (patch.WritePreview is bool wp) v.WritePreview = wp;
            if (patch.AllowShell is bool ash) v.AllowShell = ash;
            if (patch.MaxFileBytes is int mfb) v.MaxFileBytes = mfb;
            if (patch.MaxResults is int mr) v.MaxResults = mr;
            if (patch.ShellTimeoutSeconds is int sts) v.ShellTimeoutSeconds = sts;
            if (patch.GranularTools is bool gt) v.GranularTools = gt;
            if (patch.ForcePlan is bool fp) v.ForcePlan = fp;
            if (patch.ReadFileSummaryThresholdBytes is int rsb) v.ReadFileSummaryThresholdBytes = rsb;
            if (patch.MaxToolResultChars is int mtrc) v.MaxToolResultChars = mtrc;
            if (patch.AllowCliDelegation is bool acd) v.AllowCliDelegation = acd;
            if (patch.ClaudeCliPath is not null) v.ClaudeCliPath = patch.ClaudeCliPath;
            if (patch.CodexCliPath is not null) v.CodexCliPath = patch.CodexCliPath;
            if (patch.CopilotCliPath is not null) v.CopilotCliPath = patch.CopilotCliPath;
            // Persist on every save so the durable slice (cwd, CLI paths, delegation flag, and the
            // behaviour / limit knobs — see RuntimeConfigStore) survives restart. The security
            // permission toggles (AllowShell / AllowWrite / ReadOnly / AllowAnyPath / WritePreview)
            // are excluded from the persisted snapshot, so they stay session-scoped regardless.
            try { await runtimeStore.SaveAsync(ct).ConfigureAwait(false); }
            catch (Exception) { /* best-effort; in-memory mutation already applied */ }
            return Results.Json(new ToolsSettingsView(
                v.WorkingDirectory, v.AllowAnyPath, v.ReadOnly, v.AllowWrite, v.WritePreview,
                v.AllowShell, v.MaxFileBytes, v.MaxResults, v.ShellTimeoutSeconds,
                v.GranularTools, v.ForcePlan, v.ReadFileSummaryThresholdBytes, v.MaxToolResultChars,
                v.AllowCliDelegation, v.ClaudeCliPath, v.CodexCliPath, v.CopilotCliPath), JsonOpts.Default);
        });

        // ──────────────────────────── pending writes ────────────────────────────

        group.MapGet("/pending-writes", (PendingWriteStore store) =>
        {
            var rows = store.All().Select(c => new PendingWriteView(
                c.AbsolutePath,
                c.DisplayPath,
                c.OldContent.Length,
                c.NewContent.Length,
                c.StagedAt,
                PendingWriteStore.RenderUnifiedDiff(c.DisplayPath, c.OldContent, c.NewContent)));
            return Results.Json(rows, JsonOpts.Default);
        });

        group.MapPost("/pending-writes/confirm", async (
            ConfirmPathBody body,
            PendingWriteStore store,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Path)) return Results.BadRequest(new { error = "path required" });
            var result = await store.ConfirmAsync(body.Path, ct).ConfigureAwait(false);
            return Results.Json(new { applied = !result.StartsWith("Error:", StringComparison.Ordinal), result }, JsonOpts.Default);
        });

        group.MapPost("/pending-writes/discard", (ConfirmPathBody body, PendingWriteStore store) =>
        {
            if (string.IsNullOrWhiteSpace(body.Path)) return Results.BadRequest(new { error = "path required" });
            var removed = store.Remove(body.Path);
            return Results.Json(new { discarded = removed }, JsonOpts.Default);
        });

        // ──────────────────────────── plan ────────────────────────────

        group.MapGet("/plan/{jobId}", (string jobId, PlanStore store) =>
        {
            var plan = store.Get(jobId);
            if (plan is null) return Results.Json(new PlanView(jobId, DateTimeOffset.MinValue, DateTimeOffset.MinValue, Array.Empty<PlanStepView>()), JsonOpts.Default);
            var steps = plan.Steps.Select(s => new PlanStepView(s.Description, s.Status, s.Note)).ToList();
            return Results.Json(new PlanView(jobId, plan.CreatedAt, plan.UpdatedAt, steps), JsonOpts.Default);
        });

        // ──────────────────────────── folder browser ────────────────────────────

        // Lists subdirectories under a path so the UI's folder picker can let the user
        // navigate the SERVER's filesystem (we may be remote). Returns drives when given an
        // empty/null path on Windows; returns the root listing on Linux. Auth is required
        // (the /agent/browse JSON endpoint is not in BypassPaths) — but obviously anyone with
        // a key can enumerate folders the server's user can see, which is the whole point.
        group.MapGet("/browse", (string? path) =>
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                // Windows roots: list ready drives. Linux/macOS: jump straight to "/".
                if (OperatingSystem.IsWindows())
                {
                    var drives = DriveInfo.GetDrives()
                        .Where(d => d.IsReady)
                        .Select(d => new
                        {
                            name = d.RootDirectory.FullName,
                            path = d.RootDirectory.FullName,
                            isDirectory = true,
                        })
                        .ToList();
                    return Results.Json(new
                    {
                        path = "",
                        parent = (string?)null,
                        entries = drives,
                    }, JsonOpts.Default);
                }
                path = "/";
            }

            DirectoryInfo info;
            try { info = new DirectoryInfo(path); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            if (!info.Exists) return Results.NotFound(new { error = $"not a directory: {path}" });

            List<object> entries;
            try
            {
                entries = info.EnumerateDirectories()
                    .Where(d => (d.Attributes & FileAttributes.System) == 0)
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(d => (object)new { name = d.Name, path = d.FullName, isDirectory = true })
                    .ToList();
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new { path = info.FullName, parent = info.Parent?.FullName, entries = Array.Empty<object>(), error = "access denied" }, JsonOpts.Default);
            }

            return Results.Json(new
            {
                path = info.FullName,
                parent = info.Parent?.FullName,
                entries,
            }, JsonOpts.Default);
        });

        // ──────────────────────────── working dirs ────────────────────────────

        group.MapGet("/working-directories", async (IJobStore store, HostLaunchInfo launchInfo, IOptions<ToolsOptions> toolsOpts, CancellationToken ct) =>
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirs = new List<string>();
            void Push(string? p)
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                if (seen.Add(p)) dirs.Add(p);
            }
            Push(toolsOpts.Value.WorkingDirectory);
            Push(launchInfo.OriginalWorkingDirectory);
            try
            {
                var recent = await store.ListAsync(50, ct).ConfigureAwait(false);
                foreach (var row in recent)
                {
                    var s = await store.LoadAsync(row.Id, ct).ConfigureAwait(false);
                    if (s is not null) Push(s.WorkingDirectory);
                    if (dirs.Count >= 12) break;
                }
            }
            catch { /* best-effort recent-dir scrape; not fatal for the UI */ }
            return Results.Json(dirs, JsonOpts.Default);
        });

        return app;
    }

    private static (bool enabled, string? reason) ClassifyBuiltInEnablement(string toolName, ToolsOptions opts)
    {
        var category = ToolCategorizer.CategoryFor(toolName);
        if (opts.ReadOnly && category is ToolCategorizer.Category.Edit or ToolCategorizer.Category.Shell)
            return (false, "ReadOnly is set — all mutating tools are disabled");
        if (category == ToolCategorizer.Category.Edit && !opts.AllowWrite)
            return (false, "AllowWrite is off");
        if (category == ToolCategorizer.Category.Shell && !opts.AllowShell)
            return (false, "AllowShell is off");
        if (category == ToolCategorizer.Category.Edit && opts.WritePreview && toolName is "write_file" or "edit_file")
            return (true, "WritePreview on — calls stage a diff instead of writing immediately");
        return (true, null);
    }

    private static object ToEndpointView(EndpointConfig e) => new
    {
        id = e.Id,
        displayName = e.DisplayName,
        provider = e.Provider,
        baseUrl = e.BaseUrl,
        // Mask the key on read so the Settings UI can show "set / unset" without leaking it.
        // The actual value is only sent back when the user types a new one into the PATCH.
        apiKeyMasked = string.IsNullOrEmpty(e.ApiKey) ? "" : "•••• " + e.ApiKey[^Math.Min(4, e.ApiKey.Length)..],
        hasApiKey = !string.IsNullOrEmpty(e.ApiKey),
        defaultModel = e.DefaultModel,
        requestTimeoutSeconds = e.RequestTimeoutSeconds,
        enabled = e.Enabled,
        maxContextTokens = e.MaxContextTokens,
        maxOutputTokens = e.MaxOutputTokens,
        // CLI-flavour fields — ignored by non-CLI providers but always emitted so the UI form
        // round-trips them without losing values when switching tabs.
        claudePermissionMode = e.ClaudePermissionMode,
        claudeAllowedTools = e.ClaudeAllowedTools,
        claudeDangerouslySkipPermissions = e.ClaudeDangerouslySkipPermissions,
        codexSandbox = e.CodexSandbox,
        codexAskForApproval = e.CodexAskForApproval,
        copilotAllowAllTools = e.CopilotAllowAllTools,
        copilotAllowAllPaths = e.CopilotAllowAllPaths,
        copilotAllowAllUrls = e.CopilotAllowAllUrls,
        copilotAutopilot = e.CopilotAutopilot,
        copilotMaxAutopilotContinues = e.CopilotMaxAutopilotContinues,
        copilotNoAskUser = e.CopilotNoAskUser,
        copilotAllowedTools = e.CopilotAllowedTools,
        copilotDeniedTools = e.CopilotDeniedTools,
    };

    private static object ToTriggerSourceView(TriggerSource s) => new
    {
        id = s.Id,
        kind = s.Kind,
        mode = s.Mode.ToString(),
        filter = s.Filter,
        mcpServer = s.McpServer,
        scope = s.Scope,
        endpointId = s.EndpointId,
        model = s.Model,
    };

    private static object ToMcpServerView(McpServerConfig s) => new
    {
        name = s.Name,
        enabled = s.Enabled,
        url = s.Url,
        // Auth header is sensitive too; mask on read.
        authHeaderMasked = string.IsNullOrEmpty(s.AuthHeader) ? "" : "•••• " + s.AuthHeader[^Math.Min(4, s.AuthHeader.Length)..],
        hasAuthHeader = !string.IsNullOrEmpty(s.AuthHeader),
        command = s.Command,
        arguments = s.Arguments,
        workingDirectory = s.WorkingDirectory,
        environmentVariables = s.EnvironmentVariables,
        passthroughToCli = s.PassthroughToCli,
    };

    private static IResult ServeAsset(string relativePath)
    {
        if (!EmbeddedAssets.TryGetUiAsset(relativePath, out var bytes, out var contentType))
            return Results.NotFound();
        return Results.File(bytes, contentType);
    }

    private static IResult ServeIndexHtml(string uiPrefix)
    {
        if (!EmbeddedAssets.TryGetUiAsset("index.html", out var bytes, out _))
            return Results.NotFound();
        var html = System.Text.Encoding.UTF8.GetString(bytes).Replace("__BASE__", uiPrefix);
        return Results.Content(html, "text/html; charset=utf-8");
    }
}

internal sealed record ConfirmPathBody(string Path);

internal sealed record EndpointPatch(
    string Id,
    string? DisplayName = null,
    string? Provider = null,
    string? BaseUrl = null,
    string? ApiKey = null,
    string? DefaultModel = null,
    int? RequestTimeoutSeconds = null,
    bool? Enabled = null,
    int? MaxContextTokens = null,
    int? MaxOutputTokens = null,
    // CLI-flavour fields — only meaningful when Provider=ClaudeCli / CodexCli / CopilotCli;
    // round-tripped verbatim otherwise so a tab-flip in the UI doesn't lose configured values.
    string? ClaudePermissionMode = null,
    IReadOnlyList<string>? ClaudeAllowedTools = null,
    bool? ClaudeDangerouslySkipPermissions = null,
    string? CodexSandbox = null,
    string? CodexAskForApproval = null,
    bool? CopilotAllowAllTools = null,
    bool? CopilotAllowAllPaths = null,
    bool? CopilotAllowAllUrls = null,
    bool? CopilotAutopilot = null,
    int? CopilotMaxAutopilotContinues = null,
    bool? CopilotNoAskUser = null,
    IReadOnlyList<string>? CopilotAllowedTools = null,
    IReadOnlyList<string>? CopilotDeniedTools = null);

internal sealed record TriggerOptionsPatch(
    bool? Enabled = null,
    int? PollIntervalSeconds = null,
    string? Phrase = null,
    int? MaxJobsPerCycle = null,
    string? JobPreamble = null,
    int? MaxAutoResumeAttempts = null,
    int? MaxConcurrentJobs = null,
    IReadOnlyList<string>? AllowedAuthors = null);

internal sealed record TriggerSourcePatch(
    string Id,
    string? Kind = null,
    string? Mode = null,
    string? Filter = null,
    string? McpServer = null,
    string? Scope = null,
    string? EndpointId = null,
    string? Model = null);

internal sealed record McpServerPatch(
    string Name,
    bool? Enabled = null,
    string? Url = null,
    string? AuthHeader = null,
    string? Command = null,
    IReadOnlyList<string>? Arguments = null,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    bool? PassthroughToCli = null);

internal static class JsonOpts
{
    public static readonly JsonSerializerOptions Default = new()
    {
        // camelCase matches the rest of the API surface (ASP.NET's default Results.Json output)
        // and lets the JS UI use natural property names like `cmd.command`, `s.workingDirectory`.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}
