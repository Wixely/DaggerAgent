namespace Daggeragent.Configuration;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// Header to inspect for the API key. Defaults to X-Api-Key.
    /// </summary>
    public string HeaderName { get; set; } = "X-Api-Key";

    /// <summary>
    /// Allowlist of valid API keys. If empty (the default), authentication is disabled
    /// — convenient for localhost/dev. Populate this list (or set DAGGER_Auth__ApiKeys__0=...)
    /// before exposing the service on a network.
    /// </summary>
    public List<string> ApiKeys { get; set; } = new();

    /// <summary>
    /// Paths to skip the auth check on, regardless of whether keys are configured.
    /// Health/probe endpoints belong here so container orchestrators and Ollama clients
    /// can discover the service without a credential.
    /// </summary>
    public List<string> BypassPaths { get; set; } = new()
    {
        "/",
        "/favicon.ico",
        "/agent/healthz",
        // The Web UI HTML shell loads without a key; the JS then attaches the configured
        // header to every API call. The /agent/* JSON endpoints themselves still go through
        // the auth check when keys are configured. /agent/mobile is no longer a shell, only
        // a redirect to /agent/ui, but it stays listed: this middleware runs before the
        // route, so removing it would 401 every bookmark and home-screen shortcut left over
        // from when it was, instead of redirecting them.
        "/agent/ui",
        "/agent/mobile",
        "/api/version",
        "/v1/models",
    };
}
