using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Daggeragent.Configuration;
using Microsoft.Extensions.Logging;

namespace Daggeragent.Tools;

/// <summary>
/// <see cref="IExternalAgentLease"/> over Codex's app-server dialect rather than ACP: JSON-RPC
/// 2.0 as newline-delimited JSON on stdio (the <c>jsonrpc</c> header is omitted on the wire),
/// organised as thread → turn → item. Coded against the app-server protocol documentation
/// (developers.openai.com/codex/app-server, retrieved 2026-08-31); UNVERIFIED against a real
/// <c>codex</c> binary — none was available — so parsing is deliberately tolerant: unknown
/// notifications are ignored, item text is pulled from any of the shapes the docs show, and
/// any server request whose method ends in <c>requestApproval</c> is treated as an approval.
///
/// <para>Lifecycle mirrors <see cref="AcpAgentLease"/>: <c>initialize</c> + <c>initialized</c>,
/// one <c>thread/start</c> per lease, then a <c>turn/start</c> per delegation with item
/// notifications relayed to <see cref="IToolCallSink"/> as children of the enclosing call and
/// agent-message text collected as the result. Approvals go through the same
/// policy/<see cref="PermissionBroker"/>路 as ACP permission requests — <c>accept</c> maps to
/// allow, <c>decline</c> to reject.</para>
/// </summary>
public sealed class CodexAppServerLease : IExternalAgentLease
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Process _proc;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _requests = new();
    private readonly string _jobId;
    private readonly int _depth;
    private readonly IToolCallSink _sink;
    private readonly PermissionBroker _permissions;
    private readonly ILogger _log;
    private long _nextId;
    private string _threadId = "";
    private Task _readLoop = Task.CompletedTask;
    private long _lastUsedTicks = Stopwatch.GetTimestamp();
    private volatile bool _dead;

    // Per-turn state, valid while _turnGate is held by PromptAsync.
    private readonly object _turnState = new();
    private readonly StringBuilder _deltas = new();
    private readonly StringBuilder _finalMessages = new();
    private readonly Dictionary<string, (ToolCallEvent Started, long StartedAt)> _openItems = new(StringComparer.Ordinal);
    private string? _parentCallId;
    private bool _turnActive;
    private TaskCompletionSource<(string Status, string? Error)>? _turnDone;

    public AcpAgentConfig Config { get; }
    public bool IsAlive => !_dead && !_proc.HasExited && !_readLoop.IsCompleted;
    public TimeSpan IdleFor => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastUsedTicks));

    private CodexAppServerLease(AcpAgentConfig cfg, Process proc, string jobId, int depth, IToolCallSink sink, PermissionBroker permissions, ILogger log)
    {
        Config = cfg;
        _proc = proc;
        _jobId = jobId;
        _depth = depth;
        _sink = sink;
        _permissions = permissions;
        _log = log;
    }

    public static async Task<CodexAppServerLease> StartAsync(
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

        log.LogInformation("Codex app-server spawn: {Command} {Args} (agent={Agent}, job={JobId}, cwd={Cwd})",
            cfg.Command, string.Join(' ', cfg.Arguments), cfg.Name, jobId, cwd);
        var proc = Process.Start(psi)!;
        _ = Task.Run(async () =>
        {
            try
            {
                while (await proc.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                    log.LogDebug("Codex[{Agent}] stderr: {Line}", cfg.Name, line);
            }
            catch { /* pipe closed with the process */ }
        }, CancellationToken.None);
        proc.StandardInput.AutoFlush = true;

        var lease = new CodexAppServerLease(cfg, proc, jobId, depth, sink, permissions, log);
        lease._readLoop = Task.Run(() => lease.ReadLoopAsync(), CancellationToken.None);
        try
        {
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(TimeSpan.FromSeconds(30));

            await lease.RequestAsync("initialize", new
            {
                clientInfo = new { name = "dagger", title = "DaggerAgent", version = typeof(CodexAppServerLease).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" },
            }, handshakeCts.Token).ConfigureAwait(false);
            await lease.NotifyAsync("initialized", new { }, handshakeCts.Token).ConfigureAwait(false);

            // thread/start's extra knobs are pass-through config strings so codex-side renames
            // don't need a code change here.
            var threadParams = new Dictionary<string, object?> { ["cwd"] = cwd };
            if (!string.IsNullOrWhiteSpace(cfg.CodexSandbox)) threadParams["sandbox"] = cfg.CodexSandbox.Trim();
            if (!string.IsNullOrWhiteSpace(cfg.CodexApprovalPolicy)) threadParams["approvalPolicy"] = cfg.CodexApprovalPolicy.Trim();
            var thread = await lease.RequestAsync("thread/start", threadParams, handshakeCts.Token).ConfigureAwait(false);
            lease._threadId = thread.TryGetProperty("thread", out var t) && t.ValueKind == JsonValueKind.Object
                && t.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()! : "";
            if (lease._threadId.Length == 0) throw new InvalidOperationException("codex thread/start returned no thread id");

            log.LogInformation("Codex thread open: agent={Agent} thread={ThreadId}", cfg.Name, lease._threadId);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public async Task<string> PromptAsync(string task, string? parentCallId, CancellationToken ct)
    {
        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        try
        {
            TaskCompletionSource<(string, string?)> done;
            lock (_turnState)
            {
                _deltas.Clear();
                _finalMessages.Clear();
                _openItems.Clear();
                _parentCallId = parentCallId;
                _turnActive = true;
                done = _turnDone = new TaskCompletionSource<(string, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timeout = TimeSpan.FromSeconds(Math.Max(10, Config.PromptTimeoutSeconds));
            timeoutCts.CancelAfter(timeout);
            try
            {
                await RequestAsync("turn/start", new
                {
                    threadId = _threadId,
                    input = new object[] { new { type = "text", text = task } },
                }, timeoutCts.Token).ConfigureAwait(false);

                var (status, error) = await done.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                var text = CollectTurnText();
                _log.LogInformation("Codex delegation done: agent={Agent} status={Status} wallMs={WallMs} chars={Chars}",
                    Config.Name, status, sw.ElapsedMilliseconds, text.Length);
                if (status == "completed")
                    return text.Length > 0 ? text : $"(agent '{Config.Name}' produced no text)";
                var suffix = error is null ? "" : $": {error}";
                return text.Length > 0
                    ? $"{text}\n\n[codex turn {status}{suffix}]"
                    : $"Error: codex turn {status}{suffix}";
            }
            catch (OperationCanceledException)
            {
                // Best-effort interrupt, salvage what streamed, retire the lease.
                try { await RequestAsync("turn/interrupt", new { threadId = _threadId }, new CancellationTokenSource(2000).Token).ConfigureAwait(false); }
                catch { }
                _dead = true;
                var partial = CollectTurnText();
                var reason = ct.IsCancellationRequested
                    ? $"Codex delegation to '{Config.Name}' cancelled."
                    : $"Codex delegation to '{Config.Name}' timed out after {timeout.TotalSeconds:F0}s.";
                _log.LogWarning("Codex delegation aborted: agent={Agent} wallMs={WallMs} partialChars={Chars}",
                    Config.Name, sw.ElapsedMilliseconds, partial.Length);
                return partial.Length > 0 ? $"Error: {reason}\nPartial output:\n{partial}" : $"Error: {reason} (no output captured)";
            }
        }
        finally
        {
            lock (_turnState)
            {
                _turnActive = false;
                CloseOpenItemsLocked("DelegationEnded");
            }
            Interlocked.Exchange(ref _lastUsedTicks, Stopwatch.GetTimestamp());
            _turnGate.Release();
        }
    }

    private string CollectTurnText()
    {
        lock (_turnState)
        {
            // item/completed agentMessage text is authoritative; deltas are the fallback when
            // the turn ended before a completed message landed.
            var text = _finalMessages.Length > 0 ? _finalMessages.ToString() : _deltas.ToString();
            return text.Trim();
        }
    }

    // ───────────────────────────── wire ─────────────────────────────

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _proc.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { Dispatch(line); }
                catch (Exception ex) { _log.LogWarning(ex, "Codex[{Agent}]: failed handling line {Line}", Config.Name, Truncate(line)); }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Codex[{Agent}] read loop ended", Config.Name);
        }
        finally
        {
            _dead = true;
            foreach (var (_, tcs) in _requests)
                tcs.TrySetException(new InvalidOperationException("codex app-server connection closed"));
            _requests.Clear();
            lock (_turnState) { _turnDone?.TrySetResult(("failed", "connection closed")); }
        }
    }

    private void Dispatch(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;
        var hasId = root.TryGetProperty("id", out var idEl);
        var method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

        if (hasId && method is null)
        {
            // Response to one of our requests.
            if (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out var id)
                && _requests.TryRemove(id, out var tcs))
            {
                if (root.TryGetProperty("error", out var err))
                    tcs.TrySetException(new InvalidOperationException(
                        err.TryGetProperty("message", out var msg) ? msg.GetString() ?? "codex error" : "codex error"));
                else
                    tcs.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
            }
            return;
        }
        if (method is null) return;

        if (hasId)
        {
            // Server → client request. The only ones the protocol defines are approvals; match
            // loosely because the docs disagree on exact names between versions.
            var rawId = idEl.GetRawText();
            var @params = root.TryGetProperty("params", out var p) ? p.Clone() : default;
            if (method.Contains("requestApproval", StringComparison.OrdinalIgnoreCase)
                || method.StartsWith("approval/", StringComparison.OrdinalIgnoreCase))
            {
                _ = Task.Run(async () =>
                {
                    var decision = await DecideApprovalAsync(method, @params).ConfigureAwait(false);
                    await WriteLineAsync($"{{\"id\":{rawId},\"result\":{JsonSerializer.Serialize(decision)}}}", CancellationToken.None).ConfigureAwait(false);
                });
            }
            else
            {
                _ = WriteLineAsync($"{{\"id\":{rawId},\"error\":{{\"code\":-32601,\"message\":\"Method not supported by this client\"}}}}", CancellationToken.None);
            }
            return;
        }

        // Notification.
        switch (method)
        {
            case "item/started":
            case "item/updated":
            case "item/completed":
                OnItem(method, root);
                break;
            case "item/agentMessage/delta":
                if (root.TryGetProperty("params", out var dp) && dp.ValueKind == JsonValueKind.Object)
                {
                    var delta = dp.TryGetProperty("textDelta", out var td) && td.ValueKind == JsonValueKind.String ? td.GetString()
                        : dp.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.Object
                            && d.TryGetProperty("text", out var dt) && dt.ValueKind == JsonValueKind.String ? dt.GetString()
                        : null;
                    if (delta is not null) lock (_turnState) { if (_turnActive) _deltas.Append(delta); }
                }
                break;
            case "turn/completed":
                if (root.TryGetProperty("params", out var tp) && tp.ValueKind == JsonValueKind.Object
                    && tp.TryGetProperty("turn", out var turn) && turn.ValueKind == JsonValueKind.Object)
                {
                    var status = turn.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString()! : "completed";
                    string? error = null;
                    if (turn.TryGetProperty("error", out var te) && te.ValueKind == JsonValueKind.Object
                        && te.TryGetProperty("message", out var tm) && tm.ValueKind == JsonValueKind.String)
                    {
                        error = tm.GetString();
                    }
                    lock (_turnState) { _turnDone?.TrySetResult((status, error)); }
                }
                break;
            // thread/started, turn/started, turn/diff/updated, turn/plan/updated,
            // serverRequest/resolved, usage events … — nothing to do with them yet.
        }
    }

    private void OnItem(string method, JsonElement root)
    {
        if (!root.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object
            || !p.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        var type = item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
        var id = item.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString()! : "";
        if (id.Length == 0) return;

        lock (_turnState)
        {
            if (!_turnActive) return;

            if (type == "agentMessage")
            {
                if (method == "item/completed") _finalMessages.Append(ExtractItemText(item));
                return;
            }
            if (type is not ("commandExecution" or "fileChange" or "mcpToolCall" or "functionCall" or "webSearch")) return;

            if (method == "item/started" && !_openItems.ContainsKey(id))
            {
                var started = new ToolCallEvent(_jobId, _depth, DescribeItem(type, item), type)
                {
                    CallId = id,
                    ParentCallId = _parentCallId,
                };
                _openItems[id] = (started, Stopwatch.GetTimestamp());
                _sink.Started(started);
            }
            else if (method == "item/completed" && _openItems.Remove(id, out var entry))
            {
                var failed = item.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String && st.GetString() == "failed";
                _sink.Completed(entry.Started with
                {
                    Elapsed = Stopwatch.GetElapsedTime(entry.StartedAt),
                    Succeeded = !failed,
                    ResultChars = 0,
                    Error = failed ? "CodexItemFailed" : null,
                });
            }
        }
    }

    private void CloseOpenItemsLocked(string error)
    {
        foreach (var (_, entry) in _openItems)
        {
            _sink.Completed(entry.Started with
            {
                Elapsed = Stopwatch.GetElapsedTime(entry.StartedAt),
                Succeeded = false,
                ResultChars = 0,
                Error = error,
            });
        }
        _openItems.Clear();
    }

    private async Task<string> DecideApprovalAsync(string method, JsonElement @params)
    {
        var policy = (Config.PermissionPolicy ?? "deny").Trim().ToLowerInvariant();
        if (policy == "allow") return "accept";
        if (policy != "ask")
        {
            _log.LogInformation("Codex approval request ({Method}) from {Agent}: decline (policy:deny)", method, Config.Name);
            return "decline";
        }

        var title = DescribeApproval(method, @params);
        string? parentCallId;
        lock (_turnState) { parentCallId = _parentCallId; }
        var prompt = new PermissionPrompt(
            Guid.NewGuid().ToString("N"),
            _jobId,
            Config.Name,
            title,
            new[]
            {
                new PermissionPromptOption("accept", "Allow", "allow_once"),
                new PermissionPromptOption("decline", "Deny", "reject_once"),
            });
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, Config.PermissionTimeoutSeconds)));
        var (handled, optionId) = await _permissions.AskAsync(prompt, timeoutCts.Token).ConfigureAwait(false);
        var decision = handled && optionId == "accept" ? "accept" : "decline";
        _log.LogInformation("Codex approval request ({Method}) from {Agent}: {Decision} (via {Via})",
            method, Config.Name, decision, handled ? "host" : "policy:deny (no host to ask)");
        return decision;
    }

    private static string DescribeApproval(string method, JsonElement @params)
    {
        if (@params.ValueKind == JsonValueKind.Object)
        {
            if (@params.TryGetProperty("command", out var cmd))
            {
                if (cmd.ValueKind == JsonValueKind.Array)
                    return "run: " + string.Join(' ', cmd.EnumerateArray().Select(e => e.ToString()));
                if (cmd.ValueKind == JsonValueKind.String) return "run: " + cmd.GetString();
            }
            if (@params.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array)
            {
                var paths = changes.EnumerateArray()
                    .Select(c => c.TryGetProperty("path", out var path) ? path.GetString() : null)
                    .Where(s => !string.IsNullOrEmpty(s));
                return "edit: " + string.Join(", ", paths);
            }
            if (@params.TryGetProperty("path", out var single) && single.ValueKind == JsonValueKind.String)
                return "edit: " + single.GetString();
        }
        return method;
    }

    private static string DescribeItem(string type, JsonElement item)
    {
        if (type == "commandExecution" && item.TryGetProperty("command", out var cmd))
        {
            if (cmd.ValueKind == JsonValueKind.Array)
                return string.Join(' ', cmd.EnumerateArray().Select(e => e.ToString()));
            if (cmd.ValueKind == JsonValueKind.String) return cmd.GetString()!;
        }
        return type;
    }

    private static string ExtractItemText(JsonElement item)
    {
        if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            return text.GetString()!;
        if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object
                    && block.TryGetProperty("text", out var bt) && bt.ValueKind == JsonValueKind.String)
                {
                    sb.Append(bt.GetString());
                }
            }
            return sb.ToString();
        }
        return "";
    }

    private async Task<JsonElement> RequestAsync(string method, object @params, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[id] = tcs;
        try
        {
            await WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = (object?)null }, JsonOpts)
                    .Replace("\"params\":null", "\"params\":" + JsonSerializer.Serialize(@params, JsonOpts)),
                ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _requests.TryRemove(id, out _);
        }
    }

    private Task NotifyAsync(string method, object @params, CancellationToken ct) =>
        WriteLineAsync($"{{\"method\":{JsonSerializer.Serialize(method)},\"params\":{JsonSerializer.Serialize(@params, JsonOpts)}}}", ct);

    private async Task WriteLineAsync(string line, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _proc.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dead = true;
            _log.LogDebug(ex, "Codex[{Agent}] write failed", Config.Name);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static string Truncate(string s) => s.Length <= 240 ? s : s[..240] + "…";

    public void Dispose()
    {
        _dead = true;
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        try { _proc.Dispose(); } catch { }
    }
}