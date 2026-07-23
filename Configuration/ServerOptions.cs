namespace Daggeragent.Configuration;

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    /// <summary>
    /// Interface Kestrel binds to (Service / WindowsService modes only). Defaults to
    /// <c>localhost</c> — loopback only — so the agent API (tool-calling jobs, config CRUD, and the
    /// OpenAI/Ollama-compatible endpoints) is NOT reachable off-box by default. Set to
    /// <c>0.0.0.0</c> (all interfaces) or a specific IP to expose it on a network — and configure
    /// <see cref="AuthOptions.ApiKeys"/> when you do, or the agent is unauthenticated (the startup
    /// logs warn about this).
    /// </summary>
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5090;
    public string Path { get; set; } = "/agent";
}
