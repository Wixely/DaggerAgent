using Microsoft.Extensions.AI;

namespace Daggeragent.Mcp;

public sealed class McpToolProvider
{
    private readonly McpClientHost _host;

    public McpToolProvider(McpClientHost host)
    {
        _host = host;
    }

    public IReadOnlyList<AITool> GetTools() => _host.AllTools;
}
