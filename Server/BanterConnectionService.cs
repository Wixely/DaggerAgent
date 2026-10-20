using Banter.Client.Core;
using Banter.Protocol;
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
/// <para>The SDK's client already survives an outage on its own (jittered redial, fresh
/// signed nonce per attempt, room rejoin, attribute re-announce); this service's job is to
/// SHOW that honestly — connected / reconnecting / evicted — and to keep retrying the initial
/// connect when auto-connect races a Banter server that is still starting. The one state it
/// never retries out of is eviction: the identity was revoked or re-keyed, and a human with a
/// fresh enrolment code is the only way forward. Automating that would turn revocation into a
/// self-healing bypass.</para>
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
    // snapshot because every field flips inside the gate or from the client's event loop.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private DaggerBanterAgent? _agent;
    private string _state = "disconnected";   // disconnected | connecting | connected | reconnecting | evicted
    private int _reconnectAttempt;
    private string? _lastError;
    private string? _evictedReason;
    private DateTimeOffset? _connectedAt;
    private string? _fingerprint;             // of the key used for the LIVE connection
    private string? _connectedUser;
    private string? _connectedServer;
    private IReadOnlyList<string> _connectedRooms = [];
    private string? _connectedModel;
    private GrantedView? _granted;

    public BanterConnectionService(
        IServiceProvider services, IOptions<BanterOptions> options, ILogger<BanterConnectionService> log)
    {
        _services = services;
        _options = options;
        _log = log;
    }

    /// <summary>What the server actually granted, as opposed to what the config requested.</summary>
    public sealed record GrantedView(
        string Locality, string Clearance, IReadOnlyList<string> Skills,
        string Description, int CostTier, bool WantsDelegator, string WorkMode, string Persona);

    public sealed record StatusView(
        string State, int ReconnectAttempt, string? Server, string? User,
        IReadOnlyList<string> Rooms, string? Model, string? AuthMode, string? KeyFingerprint,
        DateTimeOffset? ConnectedAt, string? LastError, string? EvictedReason, GrantedView? Granted);

    public StatusView Status => new(
        _state,
        _reconnectAttempt,
        _connectedServer,
        _connectedUser,
        _connectedRooms,
        _connectedModel,
        _agent is null ? null : _fingerprint is null ? "password" : "key",
        _fingerprint,
        _connectedAt,
        _lastError,
        _evictedReason,
        _granted);

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
            _evictedReason = null;

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
            catch (BanterAuthException ex)
            {
                // The server refused the credential itself. Retrying cannot help, and the fix
                // is a human decision (fix the password, or enrol again) — so this error is
                // also what stops the auto-connect retry loop.
                await agent.DisposeAsync().ConfigureAwait(false);
                return Fail($"the server refused the credential for {options.User}: {ex.Message} " +
                            "(identity revoked or reissued? Enrol again with a fresh code.)");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await agent.DisposeAsync().ConfigureAwait(false);
                return Fail($"could not connect to {options.Server} as {options.User}: {ex.Message}");
            }

            // Show what the server actually granted next to what was requested, and track the
            // connection's life honestly: the client redials and re-announces on its own, and
            // an eviction is terminal by design.
            agent.AttributesSet += granted => _granted = ToGrantedView(granted);
            if (agent.EffectiveAttributes is { } already) _granted = ToGrantedView(already);
            agent.ConnectionLost += () =>
            {
                if (_state == "connected") { _state = "reconnecting"; _reconnectAttempt = 0; }
                _log.LogWarning("Banter: connection lost — redialling");
            };
            agent.ConnectionRetrying += attempt =>
            {
                _reconnectAttempt = attempt;
                if (attempt is 1 or 5 || attempt % 25 == 0)
                    _log.LogInformation("Banter: redial attempt {Attempt}", attempt);
            };
            agent.ConnectionRestored += () =>
            {
                _state = "connected";
                _reconnectAttempt = 0;
                _connectedAt = DateTimeOffset.UtcNow;
                _log.LogInformation("Banter: reconnected and re-announced");
            };
            agent.Evicted += reason => _ = Task.Run(() => OnEvictedAsync(reason));
            agent.WireConnectionEvents();

            _agent = agent;
            _state = "connected";
            _reconnectAttempt = 0;
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

    private static GrantedView ToGrantedView(AgentAnnouncePayload granted) => new(
        granted.Locality.ToString().ToLowerInvariant(),
        granted.Clearance.ToString().ToLowerInvariant(),
        granted.Skills,
        granted.Description,
        granted.CostTier,
        granted.WantsDelegator,
        granted.WorkMode.ToString(),
        granted.Persona);

    /// <summary>
    /// The server ended the session on purpose: the identity was revoked, re-keyed or removed.
    /// The client does not redial (the credential it would use is what just died), so drop the
    /// agent and park in a terminal state a human can read. No retry, deliberately — that is
    /// what revocation means.
    /// </summary>
    private async Task OnEvictedAsync(string reason)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_agent is null) return;
            await _agent.DisposeAsync().ConfigureAwait(false);
            _agent = null;
            ClearConnectionInfo();
            _state = "evicted";
            _evictedReason = reason;
            _lastError = $"evicted by the server: {reason}";
            _log.LogWarning("Banter: session evicted — {Reason}. Re-enrol to reconnect.", reason);
        }
        finally { _gate.Release(); }
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
            ClearConnectionInfo();
            _state = "disconnected";
            _log.LogInformation("Banter: disconnected");
            return true;
        }
        finally { _gate.Release(); }
    }

    private void ClearConnectionInfo()
    {
        _connectedAt = null;
        _fingerprint = null;
        _connectedUser = null;
        _connectedServer = null;
        _connectedRooms = [];
        _connectedModel = null;
        _granted = null;
        _reconnectAttempt = 0;
        _evictedReason = null;
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Value.AutoConnect) return Task.CompletedTask;

        // Fire-and-forget: a Banter server that is down must not stall or fail service startup.
        // Retried with capped, jittered backoff — auto-connect usually races a server that is
        // itself still booting — until it lands, the credential is refused (a human problem),
        // or the service stops. Progress lands in Status, which is where the UI looks.
        _ = Task.Run(async () =>
        {
            var delay = TimeSpan.FromSeconds(2);
            var cap = TimeSpan.FromSeconds(60);
            while (!_shutdown.IsCancellationRequested)
            {
                var error = await ConnectAsync(_shutdown.Token).ConfigureAwait(false);
                if (error is null || error.Contains("refused the credential") || error.Contains("already connected"))
                {
                    if (error is not null) _log.LogWarning("Banter auto-connect stopped: {Error}", error);
                    return;
                }

                _log.LogWarning("Banter auto-connect failed ({Error}); retrying in ~{Delay}s",
                    error, (int)delay.TotalSeconds);
                var wait = TimeSpan.FromTicks((long)(delay.Ticks * (0.5 + Random.Shared.NextDouble() * 0.5)));
                try { await Task.Delay(wait, _shutdown.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                delay = delay >= cap ? cap : TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, cap.Ticks));
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_shutdown.IsCancellationRequested) await _shutdown.CancelAsync().ConfigureAwait(false);
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
        _shutdown.Dispose();
    }
}
