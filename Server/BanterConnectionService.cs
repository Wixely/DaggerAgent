using Banter.Protocol.Transport;
using Daggeragent.Agent;
using Daggeragent.Configuration;
using Daggeragent.Modes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daggeragent.Server;

/// <summary>
/// Holds the service-mode Banter connection the web UI manages: one in-process
/// <see cref="DaggerBanterAgent"/> that the Banter tab can connect, disconnect and inspect.
/// The standalone `dagger banter` mode is unaffected — this exists so a `dagger serve`
/// deployment can sit in a room without a second process.
///
/// <para>Registered as a hosted service purely for lifecycle: at startup it connects only when
/// <see cref="BanterOptions.AutoConnect"/> says so, and at shutdown it leaves the room cleanly.
/// In the non-service modes hosted services never start, so this stays inert there.</para>
/// </summary>
public sealed class BanterConnectionService : IHostedService, IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly IOptions<BanterOptions> _options;
    private readonly ILogger<BanterConnectionService> _log;

    // One connect/disconnect at a time; status reads take nothing and see a consistent
    // snapshot because every field flips inside the gate.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DaggerBanterAgent? _agent;
    private string _state = "disconnected";           // disconnected | connecting | connected
    private string? _lastError;
    private DateTimeOffset? _connectedAt;
    private string? _fingerprint;                     // of the key used for the LIVE connection
    private string? _connectedUser;
    private string? _connectedServer;
    private IReadOnlyList<string> _connectedRooms = [];
    private string? _connectedModel;

    public BanterConnectionService(
        IServiceProvider services, IOptions<BanterOptions> options, ILogger<BanterConnectionService> log)
    {
        _services = services;
        _options = options;
        _log = log;
    }

    public sealed record StatusView(
        string State, string? Server, string? User, IReadOnlyList<string> Rooms, string? Model,
        string? AuthMode, string? KeyFingerprint, DateTimeOffset? ConnectedAt, string? LastError);

    public StatusView Status => new(
        _state,
        _connectedServer,
        _connectedUser,
        _connectedRooms,
        _connectedModel,
        _agent is null ? null : _fingerprint is null ? "password" : "key",
        _fingerprint,
        _connectedAt,
        _lastError);

    /// <summary>
    /// Connect with the current <see cref="BanterOptions"/>. Returns an error message instead of
    /// throwing — every caller is a UI surface that wants the reason, not a stack trace.
    /// </summary>
    public async Task<string?> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_agent is not null)
            {
                return "already connected — disconnect first to apply new settings.";
            }

            var options = _options.Value;
            _state = "connecting";
            _lastError = null;

            var (privateKey, credentialError) = await BanterSetup
                .ResolveCredentialAsync(options.Password, options.KeyFile, cancellationToken)
                .ConfigureAwait(false);
            if (credentialError is not null)
            {
                return Fail(credentialError);
            }

            var agentDefaults = _services.GetRequiredService<IOptions<AgentOptions>>().Value;
            var model = BanterSetup.DisplayModel(options);
            var rooms = BanterSetup.ResolveRooms(options);
            var agentOptions = BanterSetup.BuildAgentOptions(
                options, options.Server, options.User, options.Password, privateKey, rooms, model);

            var agent = new DaggerBanterAgent(
                agentOptions,
                _services.GetRequiredService<LlmAgent>(),
                options.Model,
                options.EndpointId,
                BanterSetup.BuildSystemPrompt(options, agentDefaults),
                _log);
            agent.TurnStarted += (room, sender) =>
                _log.LogInformation("Banter: [{Room}] answering {Sender}...", room, sender);

            try
            {
                await agent.StartAsync(BanterTransports.Client(agentOptions.Server), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await agent.DisposeAsync().ConfigureAwait(false);
                return Fail($"could not connect to {options.Server} as {options.User}: {ex.Message}");
            }

            _agent = agent;
            _state = "connected";
            _connectedAt = DateTimeOffset.UtcNow;
            _fingerprint = privateKey is null ? null : BanterSetup.Fingerprint(privateKey);
            _connectedUser = options.User;
            _connectedServer = options.Server;
            _connectedRooms = rooms;
            _connectedModel = model;
            _log.LogInformation(
                "Banter: connected to {Server} as {User} in {Rooms} ({Auth})",
                options.Server, options.User, string.Join(", ", rooms),
                privateKey is null ? "password" : $"key {_fingerprint}");
            return null;
        }
        finally { _gate.Release(); }

        string Fail(string error)
        {
            _state = "disconnected";
            _lastError = error;
            return error;
        }
    }

    /// <summary>Leave the room and drop the connection. True when there was one to drop.</summary>
    public async Task<bool> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_agent is null) return false;
            await _agent.DisposeAsync().ConfigureAwait(false);
            _agent = null;
            _state = "disconnected";
            _connectedAt = null;
            _fingerprint = null;
            _connectedUser = null;
            _connectedServer = null;
            _connectedRooms = [];
            _connectedModel = null;
            _log.LogInformation("Banter: disconnected");
            return true;
        }
        finally { _gate.Release(); }
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Value.AutoConnect) return Task.CompletedTask;

        // Fire-and-forget: a Banter server that is down must not stall or fail service startup.
        // The error lands in Status.LastError, which is exactly where the UI looks.
        _ = Task.Run(async () =>
        {
            var error = await ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            if (error is not null)
                _log.LogWarning("Banter auto-connect failed: {Error}", error);
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    async Task IHostedService.StopAsync(CancellationToken cancellationToken) =>
        await DisconnectAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
