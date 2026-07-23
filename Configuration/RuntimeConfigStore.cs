using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daggeragent.Configuration;

/// <summary>
/// Persists runtime-editable parts of the configuration — currently the multi-endpoint list
/// (<see cref="EndpointsOptions"/>) and the MCP server list (<see cref="McpOptions"/>) —
/// into a single JSON file alongside the SQLite job store. On startup, <see cref="LoadAsync"/>
/// reads the file (if present) and mutates the singleton IOptions instances so every downstream
/// consumer (ChatClientFactory, McpClientHost, …) picks up the user's saved edits.
/// On each CRUD operation the caller mutates the IOptions instance in-place and then calls
/// <see cref="SaveAsync"/> to flush the new snapshot to disk.
///
/// Living file is <c>{HostLaunchInfo.ContentRoot}/data/runtime-config.json</c> — same folder
/// as jobs.db so backups/restores keep everything together.
/// </summary>
public sealed class RuntimeConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    private readonly EndpointsOptions _endpoints;
    private readonly McpOptions _mcp;
    private readonly TriggerOptions _triggers;
    private readonly ToolsOptions _tools;
    private readonly HostLaunchInfo _launchInfo;
    private readonly ILogger<RuntimeConfigStore> _log;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    // Serialises config MUTATIONS so two concurrent CRUD calls can't lose an update via
    // read-copy-swap. Readers take nothing: mutations swap collection references atomically
    // (copy-on-write) and never touch a list in flight, so an in-progress enumeration always
    // sees a consistent snapshot. Separate from _ioLock (which SaveAsync takes) to avoid
    // re-entrancy — MutateAsync calls SaveAsync.
    private readonly SemaphoreSlim _mutateLock = new(1, 1);

    public RuntimeConfigStore(
        IOptions<EndpointsOptions> endpoints,
        IOptions<McpOptions> mcp,
        IOptions<TriggerOptions> triggers,
        IOptions<ToolsOptions> tools,
        HostLaunchInfo launchInfo,
        ILogger<RuntimeConfigStore> log)
    {
        _endpoints = endpoints.Value;
        _mcp = mcp.Value;
        _triggers = triggers.Value;
        _tools = tools.Value;
        _launchInfo = launchInfo;
        _log = log;
    }

    public string FilePath =>
        Path.Combine(_launchInfo.ContentRoot, "data", "runtime-config.json");

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FilePath))
            {
                _log.LogDebug("No runtime config at {Path} — using appsettings.json values", FilePath);
                return;
            }

            var json = await File.ReadAllTextAsync(FilePath, ct).ConfigureAwait(false);
            var snapshot = JsonSerializer.Deserialize<RuntimeConfigSnapshot>(json, JsonOpts);
            if (snapshot is null) return;

            // Endpoints — replace wholesale via reference-swap (never mutate the live list in
            // place: a concurrent turn may be enumerating it — copy-on-write). Empty list is
            // legitimate (user cleared it).
            if (snapshot.Endpoints is not null)
            {
                _endpoints.DefaultId = snapshot.Endpoints.DefaultId;
                _endpoints.Items = snapshot.Endpoints.Items.ToList();
            }

            // MCP servers — same wholesale reference-swap.
            if (snapshot.Mcp is not null)
                _mcp.Servers = snapshot.Mcp.Servers.ToList();

            // Triggers — full options block. TriggerService reads the live IOptions value
            // every cycle so mutations here are picked up without a restart (modulo the loop
            // re-checking Enabled / Sources on each cycle — see TriggerService.ExecuteAsync).
            if (snapshot.Triggers is not null)
            {
                _triggers.Enabled = snapshot.Triggers.Enabled;
                _triggers.PollIntervalSeconds = snapshot.Triggers.PollIntervalSeconds;
                _triggers.Phrase = snapshot.Triggers.Phrase;
                _triggers.MaxJobsPerCycle = snapshot.Triggers.MaxJobsPerCycle;
                _triggers.JobPreamble = snapshot.Triggers.JobPreamble;
                _triggers.MaxAutoResumeAttempts = snapshot.Triggers.MaxAutoResumeAttempts;
                _triggers.MaxConcurrentJobs = snapshot.Triggers.MaxConcurrentJobs;
                _triggers.AllowedAuthors = snapshot.Triggers.AllowedAuthors.ToList();
                _triggers.Sources = snapshot.Triggers.Sources.ToList();
            }

            // Last working directory — only this slice of ToolsOptions is persisted (other
            // toggles are intentionally session-scoped). Lets the user pick a project dir in
            // the UI once and have it stick across restarts for this agent installation.
            // The file lives under {ContentRoot}/data, so a separate copy of DaggerAgent in a
            // different folder has its own runtime-config.json and won't clobber this one.
            if (!string.IsNullOrWhiteSpace(snapshot.LastWorkingDirectory))
                _tools.WorkingDirectory = snapshot.LastWorkingDirectory;

            // Durable ToolsOptions slice: CLI binary paths + the delegation / behaviour / limit
            // knobs. Nullable fields, so an absent one leaves the appsettings baseline untouched.
            // Security permission toggles (AllowShell / AllowWrite / ReadOnly / AllowAnyPath /
            // WritePreview) are deliberately NOT persisted — they reset to the appsettings baseline
            // each run so a dangerous grant can't silently outlive the session that made it.
            if (snapshot.Tools is { } tp)
            {
                if (tp.ClaudeCliPath is not null) _tools.ClaudeCliPath = tp.ClaudeCliPath;
                if (tp.CodexCliPath is not null) _tools.CodexCliPath = tp.CodexCliPath;
                if (tp.CopilotCliPath is not null) _tools.CopilotCliPath = tp.CopilotCliPath;
                if (tp.AllowCliDelegation is bool acd) _tools.AllowCliDelegation = acd;
                if (tp.GranularTools is bool gt) _tools.GranularTools = gt;
                if (tp.ForcePlan is bool fp) _tools.ForcePlan = fp;
                if (tp.MaxFileBytes is int mfb) _tools.MaxFileBytes = mfb;
                if (tp.MaxResults is int mr) _tools.MaxResults = mr;
                if (tp.ShellTimeoutSeconds is int sts) _tools.ShellTimeoutSeconds = sts;
                if (tp.ReadFileSummaryThresholdBytes is int rsb) _tools.ReadFileSummaryThresholdBytes = rsb;
                if (tp.MaxToolResultChars is int mtrc) _tools.MaxToolResultChars = mtrc;
            }

            _log.LogInformation(
                "Loaded runtime config from {Path}: {EndpointCount} endpoint(s), {McpCount} mcp server(s), {TriggerCount} trigger source(s), cwd={Cwd}",
                FilePath, _endpoints.Items.Count, _mcp.Servers.Count, _triggers.Sources.Count,
                string.IsNullOrWhiteSpace(_tools.WorkingDirectory) ? "(none)" : _tools.WorkingDirectory);
        }
        finally { _ioLock.Release(); }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            var snapshot = new RuntimeConfigSnapshot
            {
                Endpoints = new EndpointsOptions
                {
                    DefaultId = _endpoints.DefaultId,
                    Items = _endpoints.Items.ToList(),
                },
                Mcp = new McpOptions { Servers = _mcp.Servers.ToList() },
                Triggers = new TriggerOptions
                {
                    Enabled = _triggers.Enabled,
                    PollIntervalSeconds = _triggers.PollIntervalSeconds,
                    Phrase = _triggers.Phrase,
                    MaxJobsPerCycle = _triggers.MaxJobsPerCycle,
                    JobPreamble = _triggers.JobPreamble,
                    AllowedAuthors = _triggers.AllowedAuthors.ToList(),
                    Sources = _triggers.Sources.ToList(),
                    MaxAutoResumeAttempts = _triggers.MaxAutoResumeAttempts,
                    MaxConcurrentJobs = _triggers.MaxConcurrentJobs,
                },
                LastWorkingDirectory = string.IsNullOrWhiteSpace(_tools.WorkingDirectory) ? null : _tools.WorkingDirectory,
                Tools = new ToolsPersisted
                {
                    ClaudeCliPath = _tools.ClaudeCliPath,
                    CodexCliPath = _tools.CodexCliPath,
                    CopilotCliPath = _tools.CopilotCliPath,
                    AllowCliDelegation = _tools.AllowCliDelegation,
                    GranularTools = _tools.GranularTools,
                    ForcePlan = _tools.ForcePlan,
                    MaxFileBytes = _tools.MaxFileBytes,
                    MaxResults = _tools.MaxResults,
                    ShellTimeoutSeconds = _tools.ShellTimeoutSeconds,
                    ReadFileSummaryThresholdBytes = _tools.ReadFileSummaryThresholdBytes,
                    MaxToolResultChars = _tools.MaxToolResultChars,
                },
            };

            // Atomic-ish: write to .tmp then rename. Stops a crash mid-write from corrupting
            // the runtime config — the only path that can wipe people's endpoint list.
            var tempPath = FilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(snapshot, JsonOpts), ct).ConfigureAwait(false);
            File.Move(tempPath, FilePath, overwrite: true);
        }
        finally { _ioLock.Release(); }
    }

    /// <summary>
    /// Run a config mutation under the writer lock, then persist. The mutation must use
    /// copy-on-write — build a new collection and assign it to the options property (reference
    /// swap) rather than mutating the live list in place — so concurrent readers (turns, the
    /// poller, MCP connect) never see a torn collection. Returns the mutation's result so callers
    /// can build a response view from the freshly-swapped object.
    /// </summary>
    public async Task<T> MutateAsync<T>(Func<T> mutate, CancellationToken ct = default)
    {
        await _mutateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = mutate();
            await SaveAsync(ct).ConfigureAwait(false);
            return result;
        }
        finally { _mutateLock.Release(); }
    }

    /// <summary>Void overload of <see cref="MutateAsync{T}"/>.</summary>
    public Task MutateAsync(Action mutate, CancellationToken ct = default) =>
        MutateAsync(() => { mutate(); return true; }, ct);

    private sealed class RuntimeConfigSnapshot
    {
        public EndpointsOptions? Endpoints { get; set; }
        public McpOptions? Mcp { get; set; }
        public TriggerOptions? Triggers { get; set; }
        /// <summary>
        /// Sticky working directory. Persisted so the UI's cwd field re-populates with the
        /// last value the user picked, instead of falling back to wherever the process was
        /// launched from. Per-installation (the file lives under {ContentRoot}/data).
        /// </summary>
        public string? LastWorkingDirectory { get; set; }

        /// <summary>
        /// Durable slice of ToolsOptions — CLI binary paths, the delegation flag, and the
        /// behaviour / limit knobs. Nullable fields so an absent one leaves the appsettings
        /// baseline untouched on load. Security permission toggles are intentionally excluded
        /// (kept session-scoped — see <see cref="LoadAsync"/>).
        /// </summary>
        public ToolsPersisted? Tools { get; set; }
    }

    private sealed class ToolsPersisted
    {
        public string? ClaudeCliPath { get; set; }
        public string? CodexCliPath { get; set; }
        public string? CopilotCliPath { get; set; }
        public bool? AllowCliDelegation { get; set; }
        public bool? GranularTools { get; set; }
        public bool? ForcePlan { get; set; }
        public int? MaxFileBytes { get; set; }
        public int? MaxResults { get; set; }
        public int? ShellTimeoutSeconds { get; set; }
        public int? ReadFileSummaryThresholdBytes { get; set; }
        public int? MaxToolResultChars { get; set; }
    }
}
