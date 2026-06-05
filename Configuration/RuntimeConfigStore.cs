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

            // Endpoints — replace wholesale. Empty list is legitimate (user cleared it).
            if (snapshot.Endpoints is not null)
            {
                _endpoints.DefaultId = snapshot.Endpoints.DefaultId;
                _endpoints.Items.Clear();
                foreach (var e in snapshot.Endpoints.Items) _endpoints.Items.Add(e);
            }

            // MCP servers — same wholesale-replace shape.
            if (snapshot.Mcp is not null)
            {
                _mcp.Servers.Clear();
                foreach (var s in snapshot.Mcp.Servers) _mcp.Servers.Add(s);
            }

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
                _triggers.AllowedAuthors.Clear();
                foreach (var a in snapshot.Triggers.AllowedAuthors) _triggers.AllowedAuthors.Add(a);
                _triggers.Sources.Clear();
                foreach (var s in snapshot.Triggers.Sources) _triggers.Sources.Add(s);
            }

            // Last working directory — only this slice of ToolsOptions is persisted (other
            // toggles are intentionally session-scoped). Lets the user pick a project dir in
            // the UI once and have it stick across restarts for this agent installation.
            // The file lives under {ContentRoot}/data, so a separate copy of DaggerAgent in a
            // different folder has its own runtime-config.json and won't clobber this one.
            if (!string.IsNullOrWhiteSpace(snapshot.LastWorkingDirectory))
                _tools.WorkingDirectory = snapshot.LastWorkingDirectory;

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
                },
                LastWorkingDirectory = string.IsNullOrWhiteSpace(_tools.WorkingDirectory) ? null : _tools.WorkingDirectory,
            };

            // Atomic-ish: write to .tmp then rename. Stops a crash mid-write from corrupting
            // the runtime config — the only path that can wipe people's endpoint list.
            var tempPath = FilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(snapshot, JsonOpts), ct).ConfigureAwait(false);
            File.Move(tempPath, FilePath, overwrite: true);
        }
        finally { _ioLock.Release(); }
    }

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
    }
}
