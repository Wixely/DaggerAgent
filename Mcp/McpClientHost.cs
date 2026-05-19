using Daggeragent.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace Daggeragent.Mcp;

public sealed class McpClientHost : IHostedService, IAsyncDisposable
{
    private readonly McpOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpClientHost> _log;
    private readonly Dictionary<string, McpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<AITool>> _tools = new(StringComparer.OrdinalIgnoreCase);

    public McpClientHost(IOptions<McpOptions> options, ILoggerFactory loggerFactory, ILogger<McpClientHost> log)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _log = log;
    }

    public IReadOnlyList<AITool> AllTools => _tools.Values.SelectMany(t => t).ToList();

    public IReadOnlyDictionary<string, McpClient> Clients => _clients;

    public async Task<bool> PingAsync(string serverName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(serverName, out var client)) return false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await client.PingAsync(cancellationToken: cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var server in _options.Servers)
        {
            if (!server.Enabled) continue;
            var hasUrl = !string.IsNullOrWhiteSpace(server.Url);
            var hasCmd = !string.IsNullOrWhiteSpace(server.Command);
            if (!hasUrl && !hasCmd)
            {
                _log.LogWarning("MCP server {Server} has neither Url nor Command — skipping", server.Name);
                continue;
            }

            try
            {
                IClientTransport transport;
                string label;
                if (hasUrl)
                {
                    var httpOpts = new HttpClientTransportOptions
                    {
                        Endpoint = new Uri(server.Url),
                        Name = server.Name,
                    };
                    if (!string.IsNullOrWhiteSpace(server.AuthHeader))
                    {
                        httpOpts.AdditionalHeaders = new Dictionary<string, string>
                        {
                            ["Authorization"] = server.AuthHeader,
                        };
                    }
                    transport = new HttpClientTransport(httpOpts, _loggerFactory);
                    label = server.Url;
                }
                else
                {
                    // stdio: spawn `Command` with `Arguments` and speak MCP over the child's
                    // stdin/stdout. Working dir defaults to ours; env vars are merged with the
                    // parent's at the SDK level.
                    var stdioOpts = new StdioClientTransportOptions
                    {
                        Command = server.Command,
                        Arguments = server.Arguments.Count > 0 ? server.Arguments : null,
                        Name = server.Name,
                        WorkingDirectory = string.IsNullOrWhiteSpace(server.WorkingDirectory) ? null : server.WorkingDirectory,
                        EnvironmentVariables = server.EnvironmentVariables.Count > 0
                            ? server.EnvironmentVariables.ToDictionary(kv => kv.Key, kv => (string?)kv.Value)
                            : null,
                    };
                    transport = new StdioClientTransport(stdioOpts, _loggerFactory);
                    label = $"stdio: {server.Command} {string.Join(' ', server.Arguments)}".TrimEnd();
                }

                var client = await McpClient.CreateAsync(transport, new McpClientOptions(), _loggerFactory, cancellationToken).ConfigureAwait(false);
                _clients[server.Name] = client;

                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                _tools[server.Name] = tools.Cast<AITool>().ToList();

                _log.LogInformation("MCP server {Server} connected via {Transport} with {ToolCount} tool(s)", server.Name, label, tools.Count);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to connect to MCP server {Server} (Url='{Url}', Command='{Command}')", server.Name, server.Url, server.Command);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _clients.Values)
        {
            try { await c.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "Error disposing MCP client"); }
        }
        _clients.Clear();
    }
}
