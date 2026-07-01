namespace Daggeragent.Configuration;

/// <summary>
/// Multi-endpoint configuration. Replaces the single-tenant OpenAIOptions shape — each
/// <see cref="EndpointConfig"/> describes one upstream LLM (OpenAI-compat, Anthropic native,
/// Ollama, …) and one is marked as the global default. Individual jobs can override the
/// active endpoint per turn via <c>ConversationState.EndpointId</c>.
///
/// Backward compatibility: when <see cref="Items"/> is empty, ChatClientFactory falls back
/// to <see cref="OpenAIOptions"/> verbatim, so existing appsettings.json that never knew
/// about Endpoints keep working without edits.
/// </summary>
public sealed class EndpointsOptions
{
    public const string SectionName = "Endpoints";

    /// <summary>Id of the endpoint to use when a job has no explicit override. Must match an item Id.</summary>
    public string? DefaultId { get; set; }

    /// <summary>The full set of configured endpoints.</summary>
    public List<EndpointConfig> Items { get; set; } = new();
}

public sealed class EndpointConfig
{
    /// <summary>Stable id used in URLs and to bind a job to a specific endpoint. Lowercase-hyphen recommended.</summary>
    public string Id { get; set; } = "";

    /// <summary>Friendly name for the UI (e.g. "Local LM Studio", "Claude Opus 4.7").</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Wire protocol: <c>OpenAI</c> (OpenAI Chat Completions / LM Studio / vLLM / OpenRouter /
    /// OpenWebUI / etc.), <c>Anthropic</c> (native Messages API), <c>Ollama</c>, or one of the
    /// local-CLI shims <c>ClaudeCli</c> / <c>CodexCli</c> / <c>CopilotCli</c> (each turn shells
    /// out to the installed CLI and reuses its existing auth — no API key needed).
    /// </summary>
    public string Provider { get; set; } = "OpenAI";

    /// <summary>Base URL of the endpoint. Leave blank to use the provider's default.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>API key / token. Stored in plain text inside the runtime config file — keep file ACL'd.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Default model id this endpoint should use when a turn doesn't override it.</summary>
    public string DefaultModel { get; set; } = "";

    /// <summary>Per-endpoint request timeout. Falls back to a sensible default per provider when 0.</summary>
    public int RequestTimeoutSeconds { get; set; } = 600;

    /// <summary>When false, the endpoint is kept in the config but hidden from the UI dropdown.</summary>
    public bool Enabled { get; set; } = true;

    // ── ClaudeCli-specific knobs (ignored for other providers) ────────────────────────────────

    /// <summary>
    /// Maps to Claude CLI's <c>--permission-mode</c> flag. Common values: <c>default</c>,
    /// <c>acceptEdits</c>, <c>plan</c>, <c>bypassPermissions</c>. Empty = no flag (Claude's
    /// own default applies). Set to <c>bypassPermissions</c> for an unattended trigger
    /// endpoint so git / shell prompts don't stall the run waiting for a human click.
    /// </summary>
    public string ClaudePermissionMode { get; set; } = "";

    /// <summary>
    /// Maps to Claude CLI's <c>--allowedTools</c> flag. Each entry is a tool pattern, e.g.
    /// <c>Bash(git:*)</c>, <c>Edit</c>, <c>Read</c>. Combined into a space-separated value
    /// when emitted. Empty list = no flag. Use this for a surgical alternative to
    /// <c>bypassPermissions</c> — only the listed patterns auto-approve.
    /// </summary>
    public List<string> ClaudeAllowedTools { get; set; } = new();

    /// <summary>
    /// Maps to Claude CLI's <c>--dangerously-skip-permissions</c> flag. Stronger than
    /// <see cref="ClaudePermissionMode"/>=bypassPermissions: turns off the permission system
    /// entirely. Reserve for fully-trusted internal endpoints; default false.
    /// </summary>
    public bool ClaudeDangerouslySkipPermissions { get; set; }

    // ── CodexCli-specific knobs (ignored for other providers) ─────────────────────────────────

    /// <summary>
    /// Maps to Codex CLI's <c>--sandbox</c> flag. Values: <c>read-only</c>,
    /// <c>workspace-write</c>, <c>danger-full-access</c>. Empty = Codex's own default.
    /// </summary>
    public string CodexSandbox { get; set; } = "";

    /// <summary>
    /// Maps to Codex CLI's <c>--ask-for-approval</c> flag. Values: <c>untrusted</c>,
    /// <c>on-failure</c>, <c>on-request</c>, <c>never</c>. Empty = Codex's own default.
    /// Use <c>never</c> for unattended runs.
    /// </summary>
    public string CodexAskForApproval { get; set; } = "";

    // ── CopilotCli-specific knobs (ignored for other providers) ────────────────────────────────

    /// <summary>
    /// Maps to Copilot CLI's <c>--allow-all-tools</c>. Master "no permission prompts" switch —
    /// every tool auto-approves without confirmation. Suitable for unattended trigger endpoints.
    /// Default false.
    /// </summary>
    public bool CopilotAllowAllTools { get; set; }

    /// <summary>
    /// Maps to Copilot CLI's <c>--allow-all-paths</c>. Disables Copilot's file-path
    /// verification so the spawned agent can access any path (not just the working
    /// directory / <c>--add-dir</c> allow-list). Default false.
    /// </summary>
    public bool CopilotAllowAllPaths { get; set; }

    /// <summary>
    /// Maps to Copilot CLI's <c>--allow-all-urls</c>. Allows the spawned agent to access
    /// any URL without confirmation. Default false.
    /// </summary>
    public bool CopilotAllowAllUrls { get; set; }

    /// <summary>
    /// Maps to Copilot CLI's <c>--autopilot</c>. Lets the agent keep working autonomously
    /// across multiple continues without pausing for a human. Pair with
    /// <see cref="CopilotMaxAutopilotContinues"/> to cap runaway loops. Default false.
    /// </summary>
    public bool CopilotAutopilot { get; set; }

    /// <summary>
    /// Maps to Copilot CLI's <c>--max-autopilot-continues</c>. Cap on autonomous
    /// continue-iterations when <see cref="CopilotAutopilot"/> is on. 0 = don't emit
    /// the flag (Copilot's own default applies).
    /// </summary>
    public int CopilotMaxAutopilotContinues { get; set; }

    /// <summary>
    /// Maps to Copilot CLI's <c>--allow-tool</c>. Each entry is a tool name / pattern that
    /// auto-approves without prompting. Additive to <see cref="CopilotAllowAllTools"/> (which
    /// is broader). Empty list = no flag.
    /// </summary>
    public List<string> CopilotAllowedTools { get; set; } = new();

    /// <summary>
    /// Maps to Copilot CLI's <c>--deny-tool</c>. Each entry is a tool name / pattern that is
    /// blocked outright — takes precedence over allow lists. Use for defence-in-depth even
    /// when <see cref="CopilotAllowAllTools"/> is on. Empty list = no flag.
    /// </summary>
    public List<string> CopilotDeniedTools { get; set; } = new();

    /// <summary>
    /// Maps to Copilot CLI's <c>--no-ask-user</c>. Disables the <c>ask_user</c> tool so the
    /// spawned agent can't stall waiting for interactive input. Recommended for unattended
    /// trigger endpoints alongside <see cref="CopilotAllowAllTools"/>. Default false.
    /// </summary>
    public bool CopilotNoAskUser { get; set; }
}
