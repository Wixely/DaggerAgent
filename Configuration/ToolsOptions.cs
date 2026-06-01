namespace Daggeragent.Configuration;

public sealed class ToolsOptions
{
    public const string SectionName = "Tools";

    /// <summary>
    /// Root directory all filesystem tools are constrained to. Empty (the default) means
    /// "use the working directory the process was launched from" (captured at startup,
    /// before Program.Main pins cwd to the exe directory for log/db paths).
    /// </summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>
    /// When true, filesystem tools (read_file, write_file, glob, grep, etc.) accept paths
    /// outside <see cref="WorkingDirectory"/>. Default false — the agent is jailed to the
    /// working directory to prevent the LLM from being talked into reading your home
    /// directory, writing to system folders, or deleting arbitrary files. Set true when
    /// you specifically want the agent to range freely (single-user trusted setup).
    /// </summary>
    public bool AllowAnyPath { get; set; }

    /// <summary>
    /// Master safety switch. When true, every mutating tool (writes, deletes, moves,
    /// shell exec, http POST/PUT/DELETE, memory save) is refused — even if the
    /// individual AllowWrite / AllowShell flags are true. Read tools stay available.
    /// Use this for "let the agent investigate but not change anything" runs.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary> Opt-in to write_file / edit_file tools. Off by default. </summary>
    public bool AllowWrite { get; set; }

    /// <summary>
    /// When true, write_file / edit_file stage the proposed change and return a unified
    /// diff instead of writing immediately. Apply via the confirm_write tool. Pair with
    /// AllowWrite=true. Default false (writes apply immediately).
    /// </summary>
    public bool WritePreview { get; set; }

    /// <summary> Opt-in to the exec_shell tool. Off by default. </summary>
    public bool AllowShell { get; set; }

    /// <summary> Maximum bytes a single read_file call will return. </summary>
    public int MaxFileBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary> Maximum results returned by grep / glob. </summary>
    public int MaxResults { get; set; } = 200;

    /// <summary> Shell-command timeout in seconds. </summary>
    public int ShellTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// When true, all built-in tools are exposed to the LLM directly. When false (the default),
    /// a router stage narrows the tool list per turn based on simple keyword heuristics on the
    /// latest user message — small models get a focused tool surface (~5-10 tools) instead of
    /// the full ~25, which cuts confusion and looping.
    /// </summary>
    public bool GranularTools { get; set; }

    /// <summary>
    /// When true (the default), the agent is forced to call <c>make_plan</c> before any other
    /// tool on a fresh job, and the system prompt nudges it to keep the plan up to date with
    /// <c>update_plan</c>. Helps small models stay coherent across multi-step tasks; turn off
    /// for short single-shot jobs where planning is overhead.
    /// </summary>
    public bool ForcePlan { get; set; } = true;

    /// <summary>
    /// Files larger than this trigger an LLM-summarised response from <c>read_file</c> instead
    /// of returning the raw contents. The summary is produced by the configured SummariserModel.
    /// Set to 0 (or a value above <see cref="MaxFileBytes"/>) to disable.
    /// </summary>
    public int ReadFileSummaryThresholdBytes { get; set; } = 50_000;

    /// <summary>
    /// When true, the <c>delegate_to_claude</c> and <c>delegate_to_codex</c> tools are
    /// registered — the agent can shell out to those CLIs as one-shot specialist sub-agents.
    /// Off by default because the spawned CLI runs with its own auth (uses the user's
    /// subscription, not ours), can take arbitrary cost/time, and counts as an external
    /// process. The set of MCP servers visible to the delegated CLI is controlled per-server
    /// via <see cref="McpServerConfig.PassthroughToCli"/>.
    /// </summary>
    public bool AllowCliDelegation { get; set; }

    /// <summary>
    /// Any tool result whose string output exceeds this many UTF-16 characters is offloaded:
    /// the full payload is held in <c>ToolResultStore</c>, and the model receives a short
    /// placeholder pointing at <c>read_tool_result</c> / <c>grep_tool_result</c> / etc. instead.
    /// Stops a single MCP tool from blowing the whole context window. Roughly 4 chars ≈ 1 token,
    /// so 16 000 ≈ 4 K tokens per offload threshold. Set to 0 to disable.
    /// </summary>
    public int MaxToolResultChars { get; set; } = 16_000;
}

/// <summary>
/// Captures the user's launch cwd before Program.Main re-pins it to the exe directory
/// (which is what we want for relative log/db paths but the wrong thing for tools).
/// </summary>
public sealed class HostLaunchInfo
{
    public string OriginalWorkingDirectory { get; init; } = Environment.CurrentDirectory;
    public string ContentRoot { get; init; } = AppContext.BaseDirectory;
}
