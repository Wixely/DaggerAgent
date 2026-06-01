using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Daggeragent.Configuration;
using Daggeragent.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Daggeragent.Llm;

/// <summary>
/// <see cref="IChatClient"/> that uses an external CLI agent (Claude Code or Codex) as the
/// LLM backend. Each turn shells out to <c>claude -p</c> or <c>codex exec</c> with the latest
/// user message; the CLI does its own tool use against its own context. The session id the
/// CLI returns is cached in <see cref="CliSessionStore"/> keyed by job id, so follow-up turns
/// in the same conversation pass <c>--resume</c> and the spawned agent keeps continuity
/// rather than cold-starting every turn.
///
/// DaggerAgent's own tools (built-in, MCP) are NOT forwarded — the CLI uses its native tool
/// surface. MCP servers configured with PassthroughToCli=true are written out to the CLI's
/// native config format for each invocation, same as the delegate_to_* tools.
/// </summary>
public sealed class CliChatClient : IChatClient
{
    public enum CliKind { Claude, Codex }

    private readonly CliKind _kind;
    private readonly string _binary;
    private readonly string _sessionKey;
    private readonly string? _model;
    private readonly string? _jobId;
    private readonly TimeSpan _timeout;
    private readonly string _cwd;
    private readonly McpOptions _mcp;
    private readonly CliSessionStore _sessions;
    private readonly ILogger _log;

    public CliChatClient(
        CliKind kind,
        string? model,
        string? jobId,
        TimeSpan timeout,
        string cwd,
        McpOptions mcp,
        CliSessionStore sessions,
        ILogger logger)
    {
        _kind = kind;
        _binary = kind == CliKind.Claude ? "claude" : "codex";
        _sessionKey = kind == CliKind.Claude ? "claude" : "codex";
        _model = model;
        _jobId = jobId;
        _timeout = timeout;
        _cwd = cwd;
        _mcp = mcp;
        _sessions = sessions;
        _log = logger;
    }

    public void Dispose() { }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType?.IsInstanceOfType(this) == true ? this : null;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var prompt = ExtractLatestUserPrompt(messages);
        if (string.IsNullOrWhiteSpace(prompt))
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "(no user prompt to forward to CLI)"))
            {
                ModelId = options?.ModelId ?? _model,
            };

        var text = await RunAsync(prompt, cancellationToken).ConfigureAwait(false);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = options?.ModelId ?? _model,
            FinishReason = ChatFinishReason.Stop,
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The CLI doesn't expose a usable token-by-token streaming format we can splice into
        // MEAI's update stream (Claude's stream-json is event-shaped, not delta-shaped). Run
        // the non-streaming path and yield the full reply as a single update — the UI's
        // collector aggregates updates anyway, so behaviour is the same except for liveness.
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var msg in response.Messages)
        {
            yield return new ChatResponseUpdate(msg.Role, msg.Contents)
            {
                ModelId = response.ModelId,
                FinishReason = response.FinishReason,
            };
        }
    }

    private static string ExtractLatestUserPrompt(IEnumerable<ChatMessage> messages)
    {
        ChatMessage? last = null;
        foreach (var m in messages)
        {
            if (m.Role == ChatRole.User) last = m;
        }
        if (last is null) return "";
        // Concatenate any text content parts; ignore images/tool results — the CLI's -p mode
        // takes plain text input only. Multimodal turns will lose attachments.
        if (!string.IsNullOrEmpty(last.Text)) return last.Text;
        var sb = new StringBuilder();
        foreach (var c in last.Contents)
        {
            if (c is TextContent tc && !string.IsNullOrEmpty(tc.Text))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(tc.Text);
            }
        }
        return sb.ToString();
    }

    private async Task<string> RunAsync(string prompt, CancellationToken ct)
    {
        var passthrough = _mcp.Servers.Where(s => s.Enabled && s.PassthroughToCli).ToList();
        var resumeSession = _jobId is null ? null : _sessions.Get(_jobId, _sessionKey);

        var tempDir = Path.Combine(Path.GetTempPath(),
            "dagger-cli-" + Guid.NewGuid().ToString("N")[..16]);
        Directory.CreateDirectory(tempDir);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _binary,
                WorkingDirectory = _cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (_kind == CliKind.Claude)
            {
                var configPath = Path.Combine(tempDir, "claude-mcp.json");
                await File.WriteAllTextAsync(configPath, CliMcpConfigBuilder.BuildClaudeConfig(passthrough), ct).ConfigureAwait(false);
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(prompt);
                psi.ArgumentList.Add("--output-format");
                psi.ArgumentList.Add("json");
                psi.ArgumentList.Add("--mcp-config");
                psi.ArgumentList.Add(configPath);
                if (!string.IsNullOrWhiteSpace(_model))
                {
                    psi.ArgumentList.Add("--model");
                    psi.ArgumentList.Add(_model);
                }
                if (!string.IsNullOrWhiteSpace(resumeSession))
                {
                    psi.ArgumentList.Add("--resume");
                    psi.ArgumentList.Add(resumeSession!);
                }
            }
            else // Codex
            {
                var configPath = Path.Combine(tempDir, "config.toml");
                await File.WriteAllTextAsync(configPath, CliMcpConfigBuilder.BuildCodexConfig(passthrough), ct).ConfigureAwait(false);
                psi.Environment["CODEX_HOME"] = tempDir;
                psi.ArgumentList.Add("exec");
                if (!string.IsNullOrWhiteSpace(resumeSession))
                {
                    psi.ArgumentList.Add("resume");
                    psi.ArgumentList.Add(resumeSession!);
                }
                psi.ArgumentList.Add(prompt);
            }

            _log.LogInformation(
                "CLI endpoint turn: binary={Binary} job={JobId} resume={Resume} passthrough={Count}",
                _binary, _jobId ?? "(none)", resumeSession is not null, passthrough.Count);

            Process proc;
            try { proc = Process.Start(psi)!; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
            {
                return $"Error: failed to start '{_binary}' — is it installed and on PATH? ({ex.Message})";
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);
            await using var killReg = timeoutCts.Token.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch { /* race with exit */ }
            });

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            try { await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                return $"Error: '{_binary}' timed out after {_timeout.TotalSeconds:F0}s.";
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                var trimmedErr = stderr.Trim();
                if (trimmedErr.Length > 800) trimmedErr = trimmedErr[..800] + "…";
                return $"Error: {_binary} exited with code {proc.ExitCode}.\n{trimmedErr}";
            }

            return _kind == CliKind.Claude
                ? ParseClaudeJson(stdout)
                : ParseCodex(stdout);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    private string ParseClaudeJson(string stdout)
    {
        var trimmed = stdout.Trim();
        if (trimmed.Length == 0) return "(claude returned no output)";
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return trimmed;
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var msg = err.ValueKind == JsonValueKind.String ? err.GetString() : err.GetRawText();
                return $"Error from claude: {msg}";
            }
            if (_jobId is not null && doc.RootElement.TryGetProperty("session_id", out var sid))
            {
                var sessionId = sid.GetString();
                if (!string.IsNullOrWhiteSpace(sessionId))
                    _sessions.Set(_jobId, _sessionKey, sessionId);
            }
            if (doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
                return result.GetString() ?? "";
        }
        catch (JsonException) { /* fall through */ }
        return trimmed;
    }

    private string ParseCodex(string stdout)
    {
        var trimmed = stdout.TrimEnd();
        if (trimmed.Length == 0) return "(codex returned no output)";

        // Heuristic 1: trailing JSON line with session_id.
        var lastNewline = trimmed.LastIndexOf('\n');
        var lastLine = lastNewline >= 0 ? trimmed[(lastNewline + 1)..] : trimmed;
        if (_jobId is not null && lastLine.StartsWith('{') && lastLine.EndsWith('}'))
        {
            try
            {
                using var doc = JsonDocument.Parse(lastLine);
                if (doc.RootElement.TryGetProperty("session_id", out var sid))
                {
                    var sessionId = sid.GetString();
                    if (!string.IsNullOrWhiteSpace(sessionId))
                        _sessions.Set(_jobId, _sessionKey, sessionId);
                }
            }
            catch (JsonException) { /* ignore */ }
        }

        // Heuristic 2: a `Session: <id>` line anywhere in the output.
        if (_jobId is not null)
        {
            var sessionLineIdx = trimmed.IndexOf("Session:", StringComparison.OrdinalIgnoreCase);
            if (sessionLineIdx >= 0)
            {
                var nl = trimmed.IndexOf('\n', sessionLineIdx);
                var line = nl >= 0 ? trimmed[sessionLineIdx..nl] : trimmed[sessionLineIdx..];
                var parts = line.Split(':', 2);
                if (parts.Length == 2)
                {
                    var sessionId = parts[1].Trim();
                    if (!string.IsNullOrWhiteSpace(sessionId))
                        _sessions.Set(_jobId, _sessionKey, sessionId);
                }
            }
        }

        return trimmed;
    }
}
