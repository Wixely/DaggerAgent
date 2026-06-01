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
    /// local-CLI shims <c>ClaudeCli</c> / <c>CodexCli</c> (each turn shells out to the
    /// installed CLI and reuses its existing auth — no API key needed).
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
}
