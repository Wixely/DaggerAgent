using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using AgentClientProtocol;
using Daggeragent.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daggeragent.Tools;

/// <summary>
/// Tools that delegate a task to an external agent over ACP (Agent Client Protocol — JSON-RPC
/// 2.0 over the child's stdio), one <c>delegate_to_acp_&lt;name&gt;</c> tool per configured
/// <see cref="AcpAgentConfig"/>. This is the client side of ACP: DaggerAgent spawns the agent
/// process, drives it via <see cref="ClientSideConnection"/>, and services its callbacks
/// (permission requests, session updates) through <see cref="HostAcpClient"/> — the inverse of
/// <c>Modes/AcpRunner</c>, where DaggerAgent is the agent and an editor is the client.
///
/// <para>Where the CLI delegations spawn a fresh one-shot process per call and juggle session
/// ids to fake continuity, an ACP agent is held open in <see cref="AcpConnectionPool"/> keyed
/// by (job, agent, cwd): successive delegations in the same job are prompts into one live
/// session, so context accumulates for free and there is no per-call spawn cost.</para>
///
/// <para>The agent's session updates feed the same <see cref="IToolCallSink"/> pipeline the
/// rest of the codebase uses — each of its tool calls becomes a child ToolCallEvent under the
/// enclosing delegate call, so the web UI's tool_progress feed shows
/// <c>delegate_to_acp_x ↳ read_file</c> live with no UI changes.</para>
/// </summary>
public sealed class AcpDelegationTools
{
    private readonly ToolsOptions _toolsOptions;
    private readonly AcpConnectionPool _pool;
    private readonly HostLaunchInfo _launchInfo;
    private readonly ILogger<AcpDelegationTools> _log;

    public AcpDelegationTools(
        IOptions<ToolsOptions> toolsOptions,
        AcpConnectionPool pool,
        HostLaunchInfo launchInfo,
        ILogger<AcpDelegationTools> log)
    {
        _toolsOptions = toolsOptions.Value;
        _pool = pool;
        _launchInfo = launchInfo;
        _log = log;
    }

    public IEnumerable<AITool> Build(string? parentJobId, int depth = 0)
    {
        // Same master gate as the CLI delegations — an ACP agent is the same trust class
        // (arbitrary external process doing real work on the user's machine).
        if (!_toolsOptions.AllowCliDelegation) yield break;

        var jobId = parentJobId ?? "";
        foreach (var configured in _toolsOptions.AcpAgents)
        {
            if (!configured.Enabled
                || string.IsNullOrWhiteSpace(configured.Name)
                || string.IsNullOrWhiteSpace(configured.Command))
            {
                continue;
            }
            var cfg = configured;

            async Task<string> DelegateToAcpAgent(
                [Description("Task or question to delegate. The agent keeps its session across calls in this job — earlier delegations are context it remembers.")] string task,
                [Description("Working directory override. Defaults to DaggerAgent's current working directory. Changing it starts a separate session.")] string? workingDirectory = null,
                [Description("Tear down any live session for this job and start the agent fresh. Default false.")] bool freshSession = false,
                CancellationToken cancellationToken = default)
            {
                var cwd = ResolveCwd(workingDirectory);
                // The enclosing call id — the same ambient source NotifyingAIFunction reads —
                // so the agent's own tool calls nest under this delegation in a host's display.
                var parentCallId = FunctionInvokingChatClient.CurrentContext?.CallContent.CallId;
                try
                {
                    var lease = await _pool.GetOrCreateAsync(cfg, jobId, depth, cwd, freshSession, cancellationToken).ConfigureAwait(false);
                    return await lease.PromptAsync(task, parentCallId, cancellationToken).ConfigureAwait(false);
                }
                catch (AcpException ex)
                {
                    _log.LogError(ex, "ACP delegation to {Agent} failed (job={JobId})", cfg.Name, jobId);
                    return $"Error: ACP agent '{cfg.Name}' returned an error: {ex.Message}";
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
                {
                    _log.LogError(ex, "Failed to start ACP agent {Command}", cfg.Command);
                    return $"Error: failed to start '{cfg.Command}' — is the path correct? ({ex.Message})";
                }
            }

            yield return AIFunctionFactory.Create(DelegateToAcpAgent,
                name: $"delegate_to_acp_{SanitizeName(cfg.Name)}",
                description:
                $"Delegate a task to the external agent '{cfg.Name}' over ACP. Unlike the one-shot CLI " +
                "delegations, this agent stays alive between calls: successive delegations in the same job " +
                "continue one session, so it remembers earlier tasks. It has a fresh context otherwise — " +
                "pass enough detail in `task` to make it actionable. Returns the agent's final answer text.");
        }
    }

    private static string SanitizeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Trim().ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    /// <summary>Mirrors CliDelegationTools.ResolveCwd — same precedence, same reasons.</summary>
    private string ResolveCwd(string? workingDirectoryOverride)
    {
        var configured = ToolExecutionContext.WorkingDirectory ?? _toolsOptions.WorkingDirectory;
        return !string.IsNullOrWhiteSpace(workingDirectoryOverride)
            ? workingDirectoryOverride!
            : (!string.IsNullOrWhiteSpace(configured) ? configured : _launchInfo.OriginalWorkingDirectory);
    }
}

/// <summary>
/// One live delegated agent, whatever wire protocol it speaks: process + connection + session,
/// one prompt at a time. <see cref="AcpAgentLease"/> is the ACP shape,
/// <see cref="CodexAppServerLease"/> the Codex app-server one.
/// </summary>
public interface IExternalAgentLease : IDisposable
{
    AcpAgentConfig Config { get; }
    bool IsAlive { get; }
    TimeSpan IdleFor { get; }
    Task<string> PromptAsync(string task, string? parentCallId, CancellationToken ct);
}

/// <summary>
/// Live delegated-agent connections, keyed by (job, agent, cwd). A lease is one spawned child
/// process with an initialized connection and an open session; it is reused across
/// delegations in the same job and dropped after <see cref="AcpAgentConfig.IdleTimeoutSeconds"/>
/// unused, when its process dies, or when a turn times out (a wedged agent is not worth
/// reusing — the next call respawns).
/// </summary>
public sealed class AcpConnectionPool : IDisposable
{
    private readonly IToolCallSink _sink;
    private readonly PermissionBroker _permissions;
    private readonly ILogger<AcpConnectionPool> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, IExternalAgentLease> _leases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _evictor;

    public AcpConnectionPool(IToolCallSink sink, PermissionBroker permissions, ILogger<AcpConnectionPool> log)
    {
        _sink = sink;
        _permissions = permissions;
        _log = log;
        _evictor = new Timer(_ => EvictIdle(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public async Task<IExternalAgentLease> GetOrCreateAsync(
        AcpAgentConfig cfg, string jobId, int depth, string cwd, bool fresh, CancellationToken ct)
    {
        var key = $"{jobId}|{cfg.Name}|{cwd}";
        IExternalAgentLease? stale = null;
        lock (_gate)
        {
            if (_leases.TryGetValue(key, out var existing))
            {
                if (!fresh && existing.IsAlive) return existing;
                _leases.Remove(key);
                stale = existing;
            }
        }
        stale?.Dispose();

        // Spawn outside the lock — process start plus two protocol round-trips. A concurrent
        // same-key create is only possible from misuse (turns are serial per job); last one
        // in wins and the loser is disposed.
        var lease = string.Equals(cfg.Protocol?.Trim(), "codex-app-server", StringComparison.OrdinalIgnoreCase)
            ? await CodexAppServerLease.StartAsync(cfg, jobId, depth, cwd, _sink, _permissions, _log, ct).ConfigureAwait(false)
            : (IExternalAgentLease)await AcpAgentLease.StartAsync(cfg, jobId, depth, cwd, _sink, _permissions, _log, ct).ConfigureAwait(false);
        IExternalAgentLease? loser = null;
        lock (_gate)
        {
            if (_leases.TryGetValue(key, out var raced)) loser = raced;
            _leases[key] = lease;
        }
        loser?.Dispose();
        return lease;
    }

    private void EvictIdle()
    {
        List<(string Key, IExternalAgentLease Lease)> drop = new();
        lock (_gate)
        {
            foreach (var (key, lease) in _leases)
            {
                if (!lease.IsAlive || lease.IdleFor > TimeSpan.FromSeconds(Math.Max(30, lease.Config.IdleTimeoutSeconds)))
                    drop.Add((key, lease));
            }
            foreach (var (key, _) in drop) _leases.Remove(key);
        }
        foreach (var (key, lease) in drop)
        {
            _log.LogInformation("ACP pool: dropping {Key} ({Reason})", key, lease.IsAlive ? "idle" : "process gone");
            lease.Dispose();
        }
    }

    public void Dispose()
    {
        _evictor.Dispose();
        List<IExternalAgentLease> all;
        lock (_gate)
        {
            all = _leases.Values.ToList();
            _leases.Clear();
        }
        foreach (var lease in all) lease.Dispose();
    }
}

/// <summary>One live ACP child agent: process + connection + session, one prompt at a time.</summary>
public sealed class AcpAgentLease : IExternalAgentLease
{
    private readonly Process _proc;
    private readonly ClientSideConnection _conn;
    private readonly HostAcpClient _client;
    private readonly Task _readLoop;
    private readonly string _sessionId;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private long _lastUsedTicks = Stopwatch.GetTimestamp();
    private volatile bool _dead;

    public AcpAgentConfig Config { get; }
    public bool IsAlive => !_dead && !_proc.HasExited && !_readLoop.IsCompleted;
    public TimeSpan IdleFor => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastUsedTicks));

    private AcpAgentLease(AcpAgentConfig cfg, Process proc, ClientSideConnection conn, HostAcpClient client, Task readLoop, string sessionId, ILogger log)
    {
        Config = cfg;
        _proc = proc;
        _conn = conn;
        _client = client;
        _readLoop = readLoop;
        _sessionId = sessionId;
        _log = log;
    }

    public static async Task<AcpAgentLease> StartAsync(
        AcpAgentConfig cfg, string jobId, int depth, string cwd, IToolCallSink sink, PermissionBroker permissions, ILogger log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cfg.Command,
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in cfg.Arguments) psi.ArgumentList.Add(arg);

        log.LogInformation("ACP spawn: {Command} {Args} (agent={Agent}, job={JobId}, cwd={Cwd})",
            cfg.Command, string.Join(' ', cfg.Arguments), cfg.Name, jobId, cwd);
        var proc = Process.Start(psi)!;
        // stdout/stdin carry protocol frames; stderr is the agent's log channel. Drain it or
        // the pipe fills and the child blocks mid-write.
        _ = Task.Run(async () =>
        {
            try
            {
                while (await proc.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                    log.LogDebug("ACP[{Agent}] stderr: {Line}", cfg.Name, line);
            }
            catch { /* pipe closed with the process */ }
        }, CancellationToken.None);
        proc.StandardInput.AutoFlush = true;

        var client = new HostAcpClient(cfg, jobId, depth, sink, permissions, log);
        var conn = new ClientSideConnection(_ => client, proc.StandardOutput, proc.StandardInput);
        var readLoop = conn.Open();
        try
        {
            // Bound the handshake separately from the caller's token: a binary that speaks
            // something other than ACP would otherwise hang the turn for the full timeout.
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(TimeSpan.FromSeconds(30));

            var init = await conn.InitializeAsync(new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new Implementation { Name = "dagger", Title = "DaggerAgent", Version = typeof(AcpDelegationTools).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" },
                // No fs, no terminal: the delegated agent runs with its own tools in its own
                // process; this host only relays activity and answers permission prompts.
                ClientCapabilities = new ClientCapabilities(),
            }, handshakeCts.Token).ConfigureAwait(false);

            var session = await conn.NewSessionAsync(new NewSessionRequest
            {
                Cwd = cwd,
                McpServers = [],
            }, handshakeCts.Token).ConfigureAwait(false);

            log.LogInformation("ACP session open: agent={Agent} ({AgentName} v{Version}) session={SessionId}",
                cfg.Name, init.AgentInfo?.Name ?? "?", init.AgentInfo?.Version ?? "?", session.SessionId);
            return new AcpAgentLease(cfg, proc, conn, client, readLoop, session.SessionId, log);
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            conn.Dispose();
            throw;
        }
    }

    public async Task<string> PromptAsync(string task, string? parentCallId, CancellationToken ct)
    {
        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        try
        {
            _client.BeginTurn(_sessionId, parentCallId);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timeout = TimeSpan.FromSeconds(Math.Max(10, Config.PromptTimeoutSeconds));
            timeoutCts.CancelAfter(timeout);
            try
            {
                var response = await _conn.PromptAsync(new PromptRequest
                {
                    SessionId = _sessionId,
                    Prompt = [new TextContentBlock { Text = task }],
                }, timeoutCts.Token).ConfigureAwait(false);

                var text = _client.EndTurn();
                _log.LogInformation("ACP delegation done: agent={Agent} stop={Stop} wallMs={WallMs} chars={Chars}",
                    Config.Name, response.StopReason, sw.ElapsedMilliseconds, text.Length);
                if (text.Length == 0) text = $"(agent '{Config.Name}' produced no text)";
                return response.StopReason switch
                {
                    StopReason.EndTurn => text,
                    StopReason.MaxTokens => text + "\n\n[truncated: the agent hit its token limit]",
                    StopReason.MaxTurnRequests => text + "\n\n[stopped: the agent hit its turn-request limit]",
                    StopReason.Refusal => text + "\n\n[the agent refused to continue]",
                    StopReason.Cancelled => text + "\n\n[the agent reported the turn as cancelled]",
                    _ => text,
                };
            }
            catch (OperationCanceledException)
            {
                // Ask the agent to stop, salvage what streamed, and retire the lease — a turn
                // we abandoned mid-flight leaves the session in a state not worth reusing.
                try { await _conn.CancelAsync(new CancelNotification { SessionId = _sessionId }, CancellationToken.None).ConfigureAwait(false); }
                catch { }
                _dead = true;
                var partial = _client.EndTurn();
                var reason = ct.IsCancellationRequested
                    ? $"ACP delegation to '{Config.Name}' cancelled."
                    : $"ACP delegation to '{Config.Name}' timed out after {timeout.TotalSeconds:F0}s.";
                _log.LogWarning("ACP delegation aborted: agent={Agent} wallMs={WallMs} partialChars={Chars}",
                    Config.Name, sw.ElapsedMilliseconds, partial.Length);
                return partial.Length > 0
                    ? $"Error: {reason}\nPartial output:\n{partial}"
                    : $"Error: {reason} (no output captured)";
            }
        }
        finally
        {
            Interlocked.Exchange(ref _lastUsedTicks, Stopwatch.GetTimestamp());
            _turnGate.Release();
        }
    }

    public void Dispose()
    {
        _dead = true;
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        _conn.Dispose();
        try { _proc.Dispose(); } catch { }
    }
}

/// <summary>
/// The host half of the connection: what the delegated agent calls back into. Session updates
/// are folded into the current turn — agent text accumulates as the eventual tool result, and
/// the agent's tool calls are republished to <see cref="IToolCallSink"/> as children of the
/// enclosing delegation (same pattern as CliDelegationTools' ClaudeStreamRelay, different
/// wire format). Permission requests are answered from standing policy
/// (<see cref="AcpAgentConfig.AutoGrantPermissions"/>) because there is no interactive user
/// on this side to ask. File-system and terminal services are not offered — the capabilities
/// sent at initialize say so, and a peer that calls them anyway gets method-not-found.
/// </summary>
public sealed class HostAcpClient : IAcpClient
{
    private readonly AcpAgentConfig _cfg;
    private readonly string _jobId;
    private readonly int _depth;
    private readonly IToolCallSink _sink;
    private readonly PermissionBroker _permissions;
    private readonly ILogger _log;

    private readonly object _gate = new();
    private readonly StringBuilder _text = new();
    private readonly Dictionary<string, (ToolCallEvent Started, long StartedAt)> _open = new(StringComparer.Ordinal);
    private string? _sessionId;
    private string? _parentCallId;

    public HostAcpClient(AcpAgentConfig cfg, string jobId, int depth, IToolCallSink sink, PermissionBroker permissions, ILogger log)
    {
        _cfg = cfg;
        _jobId = jobId;
        _depth = depth;
        _sink = sink;
        _permissions = permissions;
        _log = log;
    }

    public void BeginTurn(string sessionId, string? parentCallId)
    {
        lock (_gate)
        {
            _sessionId = sessionId;
            _parentCallId = parentCallId;
            _text.Clear();
            _open.Clear();
        }
    }

    /// <summary>Returns the turn's accumulated agent text and closes any still-open tool events.</summary>
    public string EndTurn()
    {
        lock (_gate)
        {
            foreach (var (_, entry) in _open)
            {
                _sink.Completed(entry.Started with
                {
                    Elapsed = Stopwatch.GetElapsedTime(entry.StartedAt),
                    Succeeded = false,
                    ResultChars = 0,
                    Error = "DelegationEnded",
                });
            }
            _open.Clear();
            _sessionId = null;
            return _text.ToString().Trim();
        }
    }

    public ValueTask SessionNotificationAsync(SessionNotification notification, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_sessionId is null || notification.SessionId != _sessionId) return default;
            switch (notification.Update)
            {
                case AgentMessageChunkSessionUpdate m when m.Content is TextContentBlock t:
                    _text.Append(t.Text);
                    break;

                case ToolCallSessionUpdate tc:
                    if (_open.ContainsKey(tc.ToolCallId)) break;
                    var started = new ToolCallEvent(_jobId, _depth, tc.Title, tc.Kind.ToString().ToLowerInvariant())
                    {
                        CallId = tc.ToolCallId,
                        ParentCallId = _parentCallId,
                    };
                    _open[tc.ToolCallId] = (started, Stopwatch.GetTimestamp());
                    _sink.Started(started);
                    // Some agents report a call already finished in its first update.
                    if (tc.Status is ToolCallStatus.Completed or ToolCallStatus.Failed)
                        CompleteLocked(tc.ToolCallId, tc.Status == ToolCallStatus.Failed, resultChars: 0);
                    break;

                case ToolCallUpdateSessionUpdate up when up.Status is ToolCallStatus.Completed or ToolCallStatus.Failed:
                    var chars = 0;
                    foreach (var content in up.Content ?? [])
                    {
                        if (content is ContentToolCallContent { Content: TextContentBlock text })
                            chars += text.Text.Length;
                    }
                    CompleteLocked(up.ToolCallId, up.Status == ToolCallStatus.Failed, chars);
                    break;
            }
        }
        return default;
    }

    private void CompleteLocked(string toolCallId, bool failed, int resultChars)
    {
        if (!_open.Remove(toolCallId, out var entry)) return;
        _sink.Completed(entry.Started with
        {
            Elapsed = Stopwatch.GetElapsedTime(entry.StartedAt),
            Succeeded = !failed,
            ResultChars = resultChars,
            Error = failed ? "AcpToolFailed" : null,
        });
    }

    public async ValueTask<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken cancellationToken = default)
    {
        var policy = (_cfg.PermissionPolicy ?? "deny").Trim().ToLowerInvariant();
        string? chosen;
        string decidedBy;
        if (policy == "allow")
        {
            chosen = PickOption(request.Options, allow: true);
            decidedBy = "policy:allow";
        }
        else if (policy == "ask")
        {
            // Forward to whoever is driving this job (web stream, upstream editor). No
            // responder or no answer in time falls back to deny — an unattended delegation
            // must not grant itself rights by waiting long enough.
            var prompt = new PermissionPrompt(
                Guid.NewGuid().ToString("N"),
                _jobId,
                _cfg.Name,
                DescribeToolCall(request.ToolCall),
                request.Options.Select(o => new PermissionPromptOption(o.OptionId, o.Name, KindString(o.Kind))).ToList());
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _cfg.PermissionTimeoutSeconds)));
            var (handled, optionId) = await _permissions.AskAsync(prompt, timeoutCts.Token).ConfigureAwait(false);
            if (handled)
            {
                // A null answer from a live host is a decision (dismissed) — surface it as
                // cancelled rather than substituting a reject the human never chose.
                chosen = optionId;
                decidedBy = "host";
            }
            else
            {
                chosen = PickOption(request.Options, allow: false);
                decidedBy = "policy:deny (no host to ask)";
            }
        }
        else
        {
            chosen = PickOption(request.Options, allow: false);
            decidedBy = "policy:deny";
        }

        _log.LogInformation("ACP permission request from {Agent}: {Decision} (via {DecidedBy})",
            _cfg.Name, chosen ?? "cancelled", decidedBy);

        return new RequestPermissionResponse
        {
            Outcome = chosen is null
                ? new CancelledRequestPermissionOutcome()
                : new SelectedRequestPermissionOutcome { OptionId = chosen },
        };
    }

    private static string? PickOption(PermissionOption[] options, bool allow) => allow
        ? (options.FirstOrDefault(o => o.Kind == PermissionOptionKind.AllowOnce)
            ?? options.FirstOrDefault(o => o.Kind == PermissionOptionKind.AllowAlways))?.OptionId
        : (options.FirstOrDefault(o => o.Kind == PermissionOptionKind.RejectOnce)
            ?? options.FirstOrDefault(o => o.Kind == PermissionOptionKind.RejectAlways))?.OptionId;

    private static string KindString(PermissionOptionKind kind) => kind switch
    {
        PermissionOptionKind.AllowOnce => "allow_once",
        PermissionOptionKind.AllowAlways => "allow_always",
        PermissionOptionKind.RejectOnce => "reject_once",
        PermissionOptionKind.RejectAlways => "reject_always",
        _ => "reject_once",
    };

    /// <summary>The spec types toolCall loosely; pull a human title out of whatever arrived.</summary>
    private static string DescribeToolCall(object? toolCall)
    {
        if (toolCall is System.Text.Json.JsonElement el && el.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (el.TryGetProperty("title", out var title) && title.ValueKind == System.Text.Json.JsonValueKind.String)
                return title.GetString()!;
            if (el.TryGetProperty("toolCallId", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String)
                return id.GetString()!;
        }
        return "(tool call)";
    }

    // Declared unsupported at initialize (fs/terminal capabilities false). A peer that calls
    // them anyway gets JSON-RPC method-not-found rather than a half-working shim.
    public ValueTask<ReadTextFileResponse> ReadTextFileAsync(ReadTextFileRequest request, CancellationToken cancellationToken = default)
        => throw new AcpException("fs/read_text_file is not supported by this host", null, -32601);
    public ValueTask<WriteTextFileResponse> WriteTextFileAsync(WriteTextFileRequest request, CancellationToken cancellationToken = default)
        => throw new AcpException("fs/write_text_file is not supported by this host", null, -32601);
    public ValueTask<CreateTerminalResponse> CreateTerminalAsync(CreateTerminalRequest request, CancellationToken cancellationToken = default)
        => throw new AcpException("terminal/create is not supported by this host", null, -32601);
    public ValueTask<TerminalOutputResponse> TerminalOutputAsync(TerminalOutputRequest request, CancellationToken cancellationToken = default)
        => throw new AcpException("terminal/output is not supported by this host", null, -32601);
    public ValueTask<ReleaseTerminalResponse> ReleaseTerminalAsync(ReleaseTerminalRequest request, CancellationToken cancellationToken = default)
        => throw new AcpException("terminal/release is not supported by this host", null, -32601);
    public ValueTask<WaitForTerminalExitResponse> WaitForTerminalExitAsync(WaitForTerminalExitRequest request, CancellationToken cancellationToken = default)
        => throw new AcpException("terminal/wait_for_exit is not supported by this host", null, -32601);
    public ValueTask<KillTerminalCommandResponse> KillTerminalCommandAsync(KillTerminalCommandRequest request, CancellationToken cancellationToken = default)
        => throw new AcpException("terminal/kill is not supported by this host", null, -32601);
    public ValueTask<System.Text.Json.JsonElement> ExtMethodAsync(string method, System.Text.Json.JsonElement request, CancellationToken cancellationToken = default)
        => throw new AcpException($"Unknown method: {method}", null, -32601);
    public ValueTask ExtNotificationAsync(string method, System.Text.Json.JsonElement notification, CancellationToken cancellationToken = default)
        => default;
}