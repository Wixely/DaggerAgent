using System.Reflection;
using Daggeragent.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Daggeragent.Mcp;

public sealed class McpClientHost : IHostedService, IAsyncDisposable
{
    // Must be IOptions<T>, not IOptionsMonitor<T>: RuntimeConfigStore mutates the
    // IOptions<McpOptions> singleton in-place on load. IOptionsMonitor maintains a separate
    // cached instance per name, so reading .CurrentValue here would silently ignore every
    // server added via runtime config or the UI — exactly the "Known servers: []" warning.
    private readonly IOptions<McpOptions> _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpClientHost> _log;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, McpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<AITool>> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, McpServerConnectionInfo> _connectionStatuses = new(StringComparer.OrdinalIgnoreCase);

    public McpClientHost(IOptions<McpOptions> options, ILoggerFactory loggerFactory, ILogger<McpClientHost> log)
    {
        _options = options;
        _loggerFactory = loggerFactory;
        _log = log;
    }

    public IReadOnlyList<AITool> AllTools
    {
        get
        {
            lock (_gate)
            {
                return _tools.Values.SelectMany(t => t).ToList();
            }
        }
    }

    public IReadOnlyDictionary<string, McpClient> Clients
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, McpClient>(_clients, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public IReadOnlyList<McpServerConnectionInfo> ConnectionStatuses
    {
        get
        {
            lock (_gate)
            {
                return _connectionStatuses.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
    }

    public async Task<bool> PingAsync(string serverName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        McpClient? client;
        lock (_gate)
        {
            if (!_clients.TryGetValue(serverName, out client)) return false;
        }

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
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InitializeConfiguredServersAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _log.LogInformation("Reloading MCP servers");
            var clients = ResetConnections();
            await DisposeClientsAsync(clients).ConfigureAwait(false);
            await InitializeConfiguredServersAsync(cancellationToken).ConfigureAwait(false);
            var statuses = ConnectionStatuses;
            _log.LogInformation("MCP reload complete: {Connected}/{Total} server(s) connected",
                statuses.Count(s => string.Equals(s.Status, "connected", StringComparison.OrdinalIgnoreCase)),
                statuses.Count);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task InitializeConfiguredServersAsync(CancellationToken cancellationToken)
    {
        foreach (var server in _options.Value.Servers)
        {
            if (!server.Enabled)
            {
                SetConnectionStatus(new McpServerConnectionInfo(server.Name, "disabled", DescribeTransport(server), 0));
                continue;
            }

            var hasUrl = !string.IsNullOrWhiteSpace(server.Url);
            var hasCmd = !string.IsNullOrWhiteSpace(server.Command);
            if (!hasUrl && !hasCmd)
            {
                SetConnectionStatus(new McpServerConnectionInfo(server.Name, "skipped", "not configured", 0, "missing Url or Command"));
                _log.LogWarning("MCP server {Server} has neither Url nor Command - skipping", server.Name);
                continue;
            }

            McpClient? client = null;
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

                client = await McpClient.CreateAsync(transport, BuildClientOptions(), _loggerFactory, cancellationToken).ConfigureAwait(false);
                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                SetConnectedServer(server.Name, client, tools.Cast<AITool>().ToList(), label, tools.Count);
                client = null;

                _log.LogInformation("MCP server {Server} connected via {Transport} with {ToolCount} tool(s)", server.Name, label, tools.Count);
            }
            catch (Exception ex)
            {
                if (client is not null)
                {
                    try { await client.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception disposeEx) { _log.LogWarning(disposeEx, "Error disposing failed MCP client for {Server}", server.Name); }
                }
                SetConnectionStatus(new McpServerConnectionInfo(server.Name, "failed", DescribeTransport(server), 0, ex.Message));
                _log.LogError(ex, "Failed to connect to MCP server {Server} (Url='{Url}', Command='{Command}')", server.Name, server.Url, server.Command);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var clients = ResetConnections();
            await DisposeClientsAsync(clients).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void SetConnectedServer(string name, McpClient client, IReadOnlyList<AITool> tools, string transport, int toolCount)
    {
        lock (_gate)
        {
            _clients[name] = client;
            _tools[name] = tools;
            _connectionStatuses[name] = new McpServerConnectionInfo(name, "connected", transport, toolCount);
        }
    }

    private void SetConnectionStatus(McpServerConnectionInfo status)
    {
        lock (_gate)
        {
            _connectionStatuses[status.Name] = status;
        }
    }

    private IReadOnlyList<McpClient> ResetConnections()
    {
        lock (_gate)
        {
            var clients = _clients.Values.ToList();
            _clients.Clear();
            _tools.Clear();
            _connectionStatuses.Clear();
            return clients;
        }
    }

    private async Task DisposeClientsAsync(IReadOnlyList<McpClient> clients)
    {
        foreach (var c in clients)
        {
            try { await c.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "Error disposing MCP client"); }
        }
    }

    private static readonly Lazy<McpClientOptions> _clientOptions = new(() => new McpClientOptions
    {
        // Surfaced to MCP servers during the init handshake and printed in their logs — without
        // this the SDK falls back to the running process name (which can be the same binary as a
        // CLI sub-process spawn, e.g. claude.exe via passthrough config) and makes server-side
        // log triage harder. Pin DaggerAgent's identity unambiguously.
        ClientInfo = new Implementation
        {
            Name = "DaggerAgent",
            Version = typeof(McpClientHost).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        },
    });

    private static McpClientOptions BuildClientOptions() => _clientOptions.Value;

    private static string DescribeTransport(McpServerConfig server)
    {
        if (!string.IsNullOrWhiteSpace(server.Url)) return server.Url;
        if (!string.IsNullOrWhiteSpace(server.Command)) return $"stdio: {server.Command} {string.Join(' ', server.Arguments)}".TrimEnd();
        return "not configured";
    }
}

public sealed record McpServerConnectionInfo(
    string Name,
    string Status,
    string Transport,
    int ToolCount,
    string? Detail = null);
