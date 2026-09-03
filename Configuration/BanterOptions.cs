namespace Daggeragent.Configuration;

/// <summary>
/// Settings for Banter mode (`dagger banter`): DaggerAgent logging into a Banter room server as
/// an agent and answering with its own LLM/tool loop. See the "Banter mode" README section.
/// </summary>
public sealed class BanterOptions
{
    public const string SectionName = "Banter";

    /// <summary>Banter server endpoint, e.g. tcp://127.0.0.1:7770.</summary>
    public string Server { get; set; } = "tcp://127.0.0.1:7770";

    /// <summary>Account name to log in as.</summary>
    public string User { get; set; } = "dagger";

    /// <summary>
    /// The account password, for an agent that has one. Prefer <see cref="KeyFile"/>: an enrolled
    /// agent authenticates by signing a server-chosen nonce and its credential never travels.
    /// Exactly one of Password / KeyFile may be configured — both set is refused at startup,
    /// because whichever one is stale would silently win.
    /// </summary>
    public string Password { get; set; } = "";

    /// <summary>
    /// Path to this machine's private key, produced by `dagger banter --enrol &lt;code&gt;`.
    /// Relative paths resolve against the executable directory (like every other relative path
    /// in config). On Windows the file is DPAPI-protected for the current user; elsewhere it is
    /// the SDK's plain key file with user-only permissions.
    /// </summary>
    public string KeyFile { get; set; } = "banter.key";

    /// <summary>Rooms to join on connect. Empty means #main.</summary>
    /// <remarks>No default in the initializer: the configuration binder APPENDS to a pre-populated
    /// list, so a "#main" default plus a configured "#main" would join twice.</remarks>
    public List<string> Rooms { get; set; } = [];

    /// <summary>
    /// In mention-mode rooms: answer everything rather than only messages naming this agent.
    /// Suits a dedicated room; will get the agent throttled anywhere else.
    /// </summary>
    public bool RespondToEveryMessage { get; set; }

    /// <summary>
    /// Where this agent runs, for the server's routing rules: "local" or "frontier".
    /// DaggerAgent drives whatever endpoint it is configured with, so this is a statement about
    /// that endpoint — a box-local model is local; a hosted API is frontier. Defaults to local.
    /// </summary>
    public string Locality { get; set; } = "local";

    /// <summary>Most sensitive data this agent may receive: "public", "internal" or "sensitive".</summary>
    public string Clearance { get; set; } = "sensitive";

    /// <summary>Capability tags the room's delegator matches against. Empty means code, tools.
    /// (Empty rather than defaulted for the same binder-append reason as <see cref="Rooms"/>.)</summary>
    public List<string> Skills { get; set; } = [];

    /// <summary>Human-readable summary shown in the room's agent roster.</summary>
    public string Description { get; set; } = "";

    /// <summary>Lower is cheaper. A tie-break in delegator election and routing.</summary>
    public int CostTier { get; set; } = 1;

    /// <summary>Ask to be the room's delegator. Only honoured for agents that are eligible.</summary>
    public bool WantsDelegator { get; set; }

    /// <summary>Model for room turns. Empty falls back to OpenAI.DefaultModel.</summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// System prompt for room conversations. Empty falls back to the Agent section's prompt plus
    /// a short group-chat addendum, so the agent knows replies land in a shared room.
    /// </summary>
    public string SystemPrompt { get; set; } = "";
}
