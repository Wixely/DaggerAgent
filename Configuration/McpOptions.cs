namespace Daggeragent.Configuration;

public sealed class McpOptions
{
    public const string SectionName = "Mcp";

    public List<McpServerConfig> Servers { get; set; } = new();
}

public sealed class McpServerConfig
{
    public string Name { get; set; } = "";

    /// <summary>
    /// If set, the MCP server is contacted over HTTP using the streamable-HTTP MCP
    /// transport. Mutually exclusive with <see cref="Command"/> — Url takes priority.
    /// </summary>
    public string Url { get; set; } = "";

    /// <summary>Optional Authorization header value for HTTP servers (e.g. "Bearer …").</summary>
    public string AuthHeader { get; set; } = "";

    /// <summary>
    /// If <see cref="Url"/> is empty and this is set, the MCP server is launched as a
    /// child process and contacted over stdio (the MCP "stdio" transport). This is how
    /// most of the official MCP servers ship (npx / uvx / Python / native binaries).
    /// </summary>
    public string Command { get; set; } = "";

    /// <summary>Arguments passed to <see cref="Command"/>. Each element is one arg.</summary>
    public List<string> Arguments { get; set; } = new();

    /// <summary>Working directory for the stdio child process. Empty = inherit our own cwd.</summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>Extra environment variables for the stdio child process (merged with the parent's).</summary>
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When true, this MCP server is also exported into the config file handed to
    /// <c>delegate_to_claude</c> / <c>delegate_to_codex</c> sub-agents — so the spawned CLI
    /// gets the same tool surface DaggerAgent has. Off by default: only servers explicitly
    /// marked passthrough leak into the delegated CLI, keeping local-only / privileged
    /// servers (filesystem, secret stores, etc.) inside DaggerAgent.
    /// HTTP servers are passed through verbatim; stdio servers spawn fresh subprocesses
    /// inside the CLI, so anything stateful (sqlite, in-memory caches) won't share state.
    /// </summary>
    public bool PassthroughToCli { get; set; }

    /// <summary>Deep copy — used by the copy-on-write config mutation path so an edit produces a
    /// new object (with its own inner collections) rather than mutating one a reader may hold.</summary>
    public McpServerConfig Clone()
    {
        var c = (McpServerConfig)MemberwiseClone();
        c.Arguments = new List<string>(Arguments);
        c.EnvironmentVariables = new Dictionary<string, string>(EnvironmentVariables);
        return c;
    }
}
