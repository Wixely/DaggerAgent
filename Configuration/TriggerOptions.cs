namespace Daggeragent.Configuration;

/// <summary>
/// Background polling of MCP servers for "@dagger"-style mentions in tickets / issues /
/// work items. Disabled by default. When enabled, a BackgroundService scans each
/// configured source every <see cref="PollIntervalSeconds"/> seconds, deduplicates
/// against the trigger_state SQLite table, and fires an agent job per fresh match.
/// </summary>
public sealed class TriggerOptions
{
    public const string SectionName = "Triggers";

    /// <summary> Master on/off. Default false — Plan B is opt-in. </summary>
    public bool Enabled { get; set; }

    /// <summary> Polling interval in seconds. Default 120 (2 minutes). </summary>
    public int PollIntervalSeconds { get; set; } = 120;

    /// <summary>
    /// Phrase that wakes the agent. Default "@dagger". Match is case-insensitive.
    /// Must appear in the ticket body / comment for the source to fire a job.
    /// </summary>
    public string Phrase { get; set; } = "@dagger";

    /// <summary>
    /// Allowlist of usernames whose mentions actually trigger jobs. Empty list = anyone.
    /// Strongly recommended to populate this in production so a random commenter can't
    /// drive your agent.
    /// </summary>
    public List<string> AllowedAuthors { get; set; } = new();

    /// <summary>
    /// Sources to poll. Each entry names a configured MCP server (from Mcp.Servers)
    /// and the kind of source — which determines the call shape into
    /// list_mentions_since.
    /// </summary>
    public List<TriggerSource> Sources { get; set; } = new();

    /// <summary>
    /// Maximum number of jobs spawned in a single poll cycle, across all sources.
    /// Backstop against a flood of mentions burning through tokens.
    /// </summary>
    public int MaxJobsPerCycle { get; set; } = 5;

    /// <summary>
    /// When the agent picks up a triggered job, the seed prompt is prefixed with this.
    /// Use it to set the project context / playbook (or rely solely on dagger.md).
    /// </summary>
    public string JobPreamble { get; set; } =
        "You were triggered by a mention in a ticket. Read the details below, decide whether you can act on them, and proceed.";

    /// <summary>
    /// Maximum number of times a trigger-spawned job can be auto-resumed after the process
    /// was killed mid-turn. Capped to stop a poison job from looping on every restart.
    /// Set to 0 to disable auto-resume entirely — orphans stay paused for manual click.
    /// </summary>
    public int MaxAutoResumeAttempts { get; set; } = 3;
}

public sealed class TriggerSource
{
    /// <summary> Free-form id, e.g. "github-main". Used for SQLite dedup state. </summary>
    public string Id { get; set; } = "";

    /// <summary> "GitHub" | "GitLab" | "AzureDevOps" — picks which call shape to use. </summary>
    public string Kind { get; set; } = "";

    /// <summary>
    /// How this source decides what to surface to the agent:
    ///   Mentions — call list_mentions_since with Filter (or the global Phrase) as the substring.
    ///   Label    — call list_issues filtered by Filter as a label name.
    ///   Assignee — call list_issues filtered by Filter as a username.
    ///   AllNew   — call list_issues with no filter; every fresh issue spawns a job.
    /// Defaults to Mentions for backward compatibility with existing configs.
    /// </summary>
    public TriggerMode Mode { get; set; } = TriggerMode.Mentions;

    /// <summary>
    /// Mode-specific filter:
    ///   Mentions — substring to search for (e.g. "@bot"). If empty, falls back to global Phrase.
    ///   Label    — label name (e.g. "needs-triage"). Required.
    ///   Assignee — username (e.g. "dagger-bot"). Required.
    ///   AllNew   — ignored.
    /// </summary>
    public string Filter { get; set; } = "";

    /// <summary>
    /// Name of the MCP server (must match an entry in Mcp.Servers) that exposes the
    /// relevant tools for this source.
    /// </summary>
    public string McpServer { get; set; } = "";

    /// <summary>
    /// Free-form scope passed to the MCP tool — owner/repo for GitHub, group/project
    /// for GitLab, project name for AzureDevOps. Optional; empty falls back to the MCP
    /// server's configured default.
    /// </summary>
    public string Scope { get; set; } = "";

    /// <summary>
    /// Optional endpoint override for jobs spawned from this source. Must match an
    /// <c>Endpoints.Items[].Id</c>. When blank, the global <c>Endpoints.DefaultId</c>
    /// applies. Point at a <c>ClaudeCli</c> / <c>CodexCli</c> endpoint to delegate
    /// triggered tickets to a local CLI agent instead of an API endpoint.
    /// </summary>
    public string EndpointId { get; set; } = "";

    /// <summary>
    /// Optional model override for jobs spawned from this source. When blank, falls
    /// back to the chosen endpoint's <c>DefaultModel</c> (or, with no endpoint set,
    /// the legacy <c>OpenAI.DefaultModel</c>).
    /// </summary>
    public string Model { get; set; } = "";
}

public enum TriggerMode
{
    Mentions,
    Label,
    Assignee,
    AllNew,
}
