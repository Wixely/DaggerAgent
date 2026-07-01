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
    public enum CliKind { Claude, Codex, Copilot }

    private readonly CliKind _kind;
    private readonly string _binary;
    private readonly string _sessionKey;
    private readonly string? _model;
    private readonly string? _jobId;
    private readonly TimeSpan _timeout;
    private readonly string _cwd;
    private readonly McpOptions _mcp;
    private readonly CliSessionStore _sessions;
    private readonly PermissionFlags _permission;
    private readonly ILogger _log;

    public CliChatClient(
        CliKind kind,
        string? model,
        string? jobId,
        TimeSpan timeout,
        string cwd,
        McpOptions mcp,
        CliSessionStore sessions,
        ILogger logger,
        string? binaryPathOverride = null,
        PermissionFlags? permission = null)
    {
        _kind = kind;
        var defaultBinary = kind switch
        {
            CliKind.Claude => "claude",
            CliKind.Codex => "codex",
            CliKind.Copilot => "copilot",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        _binary = string.IsNullOrWhiteSpace(binaryPathOverride) ? defaultBinary : binaryPathOverride.Trim();
        _sessionKey = defaultBinary;
        _model = model;
        _jobId = jobId;
        _timeout = timeout;
        _cwd = cwd;
        _mcp = mcp;
        _sessions = sessions;
        _permission = permission ?? PermissionFlags.None;
        _log = logger;
    }

    /// <summary>
    /// Per-endpoint permission configuration. Maps to <c>--permission-mode</c> / <c>--allowedTools</c>
    /// / <c>--dangerously-skip-permissions</c> for Claude, <c>--sandbox</c> / <c>--ask-for-approval</c>
    /// for Codex, and <c>--allow-all-tools</c> / <c>--allow-all-paths</c> / <c>--allow-all-urls</c>
    /// / <c>--autopilot</c> / <c>--allow-tool</c> / <c>--deny-tool</c> / <c>--no-ask-user</c> for
    /// Copilot. Empty / default values mean "don't emit the flag at all" — the CLI's own default
    /// applies in that case.
    /// </summary>
    public sealed record PermissionFlags(
        string ClaudePermissionMode = "",
        IReadOnlyList<string>? ClaudeAllowedTools = null,
        bool ClaudeDangerouslySkipPermissions = false,
        string CodexSandbox = "",
        string CodexAskForApproval = "",
        bool CopilotAllowAllTools = false,
        bool CopilotAllowAllPaths = false,
        bool CopilotAllowAllUrls = false,
        bool CopilotAutopilot = false,
        int CopilotMaxAutopilotContinues = 0,
        IReadOnlyList<string>? CopilotAllowedTools = null,
        IReadOnlyList<string>? CopilotDeniedTools = null,
        bool CopilotNoAskUser = false)
    {
        public static readonly PermissionFlags None = new();
    }

    public void Dispose() { }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType?.IsInstanceOfType(this) == true ? this : null;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var msgList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var prompt = BuildPrompt(msgList);
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

    /// <summary>
    /// Pick the prompt shape based on whether a CLI session is available to resume against.
    /// With a session: only the latest user message goes over the wire — Claude's own session
    /// store rehydrates the prior context via <c>--resume</c>. Without a session: the prior
    /// conversation is folded inline so context isn't lost (e.g. when the cwd changed mid-job
    /// invalidated the session, or turn 1 errored before stashing one).
    /// </summary>
    private string BuildPrompt(IReadOnlyList<ChatMessage> messages)
    {
        var latestUser = FindLatestUser(messages);
        var latest = latestUser is null ? "" : ExtractText(latestUser);
        if (string.IsNullOrWhiteSpace(latest)) return latest;

        if (_jobId is not null && _sessions.Get(_jobId, _sessionKey, _cwd) is not null)
            return latest;

        // No usable session — see if there's prior conversation to fold in. A brand-new job
        // only has [system, user(first prompt)] so prior is empty and we send just the latest
        // (current behaviour). A continuation has assistant turns in between — include those.
        var transcript = BuildTranscript(messages, exclude: latestUser);
        if (string.IsNullOrEmpty(transcript)) return latest;

        _log.LogInformation(
            "CLI {Binary} job {JobId}: no resumable session — including {PriorChars} chars of prior conversation in the prompt",
            _binary, _jobId ?? "(none)", transcript.Length);

        return
            "[DaggerAgent: this conversation is being resumed without an active CLI session. " +
            "The prior turns are included below as context — treat them as established history " +
            "and continue from where they left off, then answer the latest user message.]\n\n" +
            "--- Prior conversation ---\n" +
            transcript +
            "\n--- Latest user message ---\n" +
            latest;
    }

    private static ChatMessage? FindLatestUser(IReadOnlyList<ChatMessage> messages)
    {
        ChatMessage? last = null;
        foreach (var m in messages)
        {
            if (m.Role == ChatRole.User) last = m;
        }
        return last;
    }

    private static string BuildTranscript(IReadOnlyList<ChatMessage> messages, ChatMessage? exclude)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            if (ReferenceEquals(m, exclude)) continue;
            // System (persona / preamble) is intentionally skipped — Claude's own system prompt
            // would clash with ours. Only User and Assistant turns are folded in as context.
            if (m.Role != ChatRole.User && m.Role != ChatRole.Assistant) continue;
            var text = ExtractText(m);
            if (string.IsNullOrEmpty(text)) continue;
            var label = m.Role == ChatRole.User ? "User" : "Assistant";
            if (sb.Length > 0) sb.AppendLine();
            sb.Append('[').Append(label).Append("]:\n").AppendLine(text);
        }
        return sb.ToString();
    }

    private static string ExtractText(ChatMessage msg)
    {
        // Concatenate any text content parts; ignore images/tool results — the CLI's -p mode
        // takes plain text input only. Multimodal turns will lose attachments.
        if (!string.IsNullOrEmpty(msg.Text)) return msg.Text;
        var sb = new StringBuilder();
        foreach (var c in msg.Contents)
        {
            if (c is TextContent tc && !string.IsNullOrEmpty(tc.Text))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(tc.Text);
            }
        }
        return sb.ToString();
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

    private async Task<string> RunAsync(string prompt, CancellationToken ct)
    {
        var passthrough = _mcp.Servers.Where(s => s.Enabled && s.PassthroughToCli).ToList();
        string? resumeSession = null;
        if (_jobId is not null)
        {
            // Cwd is part of the resume key because Claude stores sessions per project dir —
            // handing it an id from a different cwd makes it bail with "No conversation found".
            resumeSession = _sessions.Get(_jobId, _sessionKey, _cwd);
            if (resumeSession is null)
            {
                var staleCwd = _sessions.GetStoredCwd(_jobId, _sessionKey);
                if (staleCwd is not null && !string.Equals(staleCwd, _cwd, StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogInformation(
                        "CLI {Binary} job {JobId}: dropping resume session (was for cwd={OldCwd}, now cwd={NewCwd}) — starting fresh session",
                        _binary, _jobId, staleCwd, _cwd);
                    _sessions.Clear(_jobId, _sessionKey);
                }
            }
        }

        var tempDir = Path.Combine(Path.GetTempPath(),
            "dagger-cli-" + Guid.NewGuid().ToString("N")[..16]);
        Directory.CreateDirectory(tempDir);
        var wallClock = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _binary,
                WorkingDirectory = _cwd,
                // Redirect stdin so we can close it immediately — Claude Code CLI waits ~3s
                // for piped input on a non-TTY stdin (which an inherited server stdin is),
                // then exits with code 1. Closing signals EOF up-front.
                RedirectStandardInput = true,
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
                // Per-endpoint permission posture. Dangerously-skip wins over permission-mode
                // (passing both is redundant; Claude accepts either). Allowed-tools is additive
                // and useful even when a mode is set.
                if (_permission.ClaudeDangerouslySkipPermissions)
                {
                    psi.ArgumentList.Add("--dangerously-skip-permissions");
                }
                else if (!string.IsNullOrWhiteSpace(_permission.ClaudePermissionMode))
                {
                    psi.ArgumentList.Add("--permission-mode");
                    psi.ArgumentList.Add(_permission.ClaudePermissionMode.Trim());
                }
                if (_permission.ClaudeAllowedTools is { Count: > 0 } allow)
                {
                    psi.ArgumentList.Add("--allowedTools");
                    psi.ArgumentList.Add(string.Join(' ', allow.Where(s => !string.IsNullOrWhiteSpace(s))));
                }
            }
            else if (_kind == CliKind.Codex)
            {
                var configPath = Path.Combine(tempDir, "config.toml");
                await File.WriteAllTextAsync(configPath, CliMcpConfigBuilder.BuildCodexConfig(passthrough), ct).ConfigureAwait(false);
                psi.Environment["CODEX_HOME"] = tempDir;
                psi.ArgumentList.Add("exec");
                // Codex's sandbox / approval flags belong before the `resume` subcommand vs the
                // prompt itself — Codex parses global flags first, then the subcommand.
                if (!string.IsNullOrWhiteSpace(_permission.CodexSandbox))
                {
                    psi.ArgumentList.Add("--sandbox");
                    psi.ArgumentList.Add(_permission.CodexSandbox.Trim());
                }
                if (!string.IsNullOrWhiteSpace(_permission.CodexAskForApproval))
                {
                    psi.ArgumentList.Add("--ask-for-approval");
                    psi.ArgumentList.Add(_permission.CodexAskForApproval.Trim());
                }
                if (!string.IsNullOrWhiteSpace(resumeSession))
                {
                    psi.ArgumentList.Add("resume");
                    psi.ArgumentList.Add(resumeSession!);
                }
                psi.ArgumentList.Add(prompt);
            }
            else // Copilot
            {
                // Same mcpServers JSON shape as Claude — supplied per-invocation via
                // --additional-mcp-config @<path>. Copilot supports both HTTP and stdio MCP
                // servers (unlike Codex which is stdio-only), so nothing is skipped.
                var configPath = Path.Combine(tempDir, "copilot-mcp.json");
                await File.WriteAllTextAsync(configPath, CliMcpConfigBuilder.BuildCopilotConfig(passthrough), ct).ConfigureAwait(false);
                // Isolate per-invocation state (skills cache, session index) so a
                // multi-tenant DaggerAgent doesn't share auth or session ids across users.
                psi.Environment["COPILOT_HOME"] = tempDir;
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(prompt);
                psi.ArgumentList.Add("--output-format");
                psi.ArgumentList.Add("json");
                // --silent drops the human-readable header/usage block so JSONL parsing sees
                // only the model's structured events.
                psi.ArgumentList.Add("--silent");
                psi.ArgumentList.Add("--additional-mcp-config");
                psi.ArgumentList.Add("@" + configPath);
                if (!string.IsNullOrWhiteSpace(_model))
                {
                    psi.ArgumentList.Add("--model");
                    psi.ArgumentList.Add(_model);
                }
                if (!string.IsNullOrWhiteSpace(resumeSession))
                {
                    // --session-id is exact match (vs --resume which does fuzzy/prefix matching).
                    // We stored the id ourselves so we always want an exact match.
                    psi.ArgumentList.Add("--session-id");
                    psi.ArgumentList.Add(resumeSession!);
                }
                // Non-interactive mode requires --allow-all-tools (per `copilot --help`:
                // "required for non-interactive mode"). --no-ask-user is enforced for the
                // same reason — the ask_user tool would deadlock a subprocess. Both are
                // always-on regardless of endpoint config; the AllowAllTools/NoAskUser
                // fields on the endpoint stay in the DTO for round-trip compat but do not
                // gate emission of these flags.
                psi.ArgumentList.Add("--allow-all-tools");
                psi.ArgumentList.Add("--no-ask-user");
                if (_permission.CopilotAllowAllPaths) psi.ArgumentList.Add("--allow-all-paths");
                if (_permission.CopilotAllowAllUrls) psi.ArgumentList.Add("--allow-all-urls");
                if (_permission.CopilotAutopilot)
                {
                    psi.ArgumentList.Add("--autopilot");
                    if (_permission.CopilotMaxAutopilotContinues > 0)
                    {
                        psi.ArgumentList.Add("--max-autopilot-continues");
                        psi.ArgumentList.Add(_permission.CopilotMaxAutopilotContinues.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
                if (_permission.CopilotAllowedTools is { Count: > 0 } cAllow)
                {
                    // Copilot takes --allow-tool as a repeatable single-value flag (one tool per flag).
                    foreach (var t in cAllow.Where(s => !string.IsNullOrWhiteSpace(s)))
                    {
                        psi.ArgumentList.Add("--allow-tool");
                        psi.ArgumentList.Add(t);
                    }
                }
                if (_permission.CopilotDeniedTools is { Count: > 0 } cDeny)
                {
                    foreach (var t in cDeny.Where(s => !string.IsNullOrWhiteSpace(s)))
                    {
                        psi.ArgumentList.Add("--deny-tool");
                        psi.ArgumentList.Add(t);
                    }
                }
            }

            _log.LogInformation(
                "CLI endpoint turn: binary={Binary} job={JobId} resume={Resume} passthrough={Count} cwd={Cwd} model={Model} promptChars={PromptChars}",
                _binary, _jobId ?? "(none)", resumeSession is not null, passthrough.Count, _cwd, _model ?? "(default)", prompt.Length);
            _log.LogDebug(
                "CLI endpoint args: {Args}",
                FormatArgsForLog(psi.ArgumentList));

            Process proc;
            try { proc = Process.Start(psi)!; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
            {
                _log.LogError(ex, "Failed to start CLI binary {Binary}", _binary);
                return $"Error: failed to start '{_binary}' — is it installed and on PATH? ({ex.Message})";
            }

            try { proc.StandardInput.Close(); }
            catch (Exception ex) { _log.LogDebug(ex, "Closing stdin for {Binary} failed (likely already closed)", _binary); }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);
            await using var killReg = timeoutCts.Token.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch { /* race with exit */ }
            });

            // Read the pipes WITHOUT passing the timeout token — on timeout the kill registration
            // closes the pipes naturally and these tasks complete with whatever Claude had buffered.
            // Passing the token would race the kill: the reader could cancel before the pipes drain,
            // leaving us with empty stdout/stderr and no diagnostic data on timeout.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            try { await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                // Capture whatever Claude managed to write before we killed it — a hung run is
                // useless to debug without knowing what it printed. Use a short bounded wait
                // because the pipes should already be closing as the kill propagates.
                wallClock.Stop();
                var partialStdout = await ReadPartialAsync(stdoutTask, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                var partialStderr = await ReadPartialAsync(stderrTask, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                _log.LogError(
                    "CLI {Binary} job {JobId} timed out after {TimeoutSec}s (wallMs={WallMs}). partialStdoutChars={StdoutChars} partialStderrChars={StderrChars} partialStderr={Stderr} partialStdout={Stdout}",
                    _binary, _jobId ?? "(none)", _timeout.TotalSeconds, wallClock.ElapsedMilliseconds,
                    partialStdout.Length, partialStderr.Length,
                    Truncate(partialStderr, 2000), Truncate(partialStdout, 2000));
                var bits = new List<string>();
                var trimmedErr = partialStderr.Trim();
                if (trimmedErr.Length > 0) bits.Add($"stderr: {Truncate(trimmedErr, 600)}");
                var trimmedOut = partialStdout.Trim();
                if (trimmedOut.Length > 0) bits.Add($"stdout: {Truncate(trimmedOut, 600)}");
                if (bits.Count == 0) bits.Add("(no stdout / stderr captured — claude.exe was idle or stuck before printing anything; check auth / network / MCP transport)");
                return $"Error: '{_binary}' timed out after {_timeout.TotalSeconds:F0}s.\n{string.Join("\n", bits)}";
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                // Claude often writes diagnostics to stdout (even on failure, in --output-format json
                // mode it sometimes prints a JSON error there). Log both streams so we don't lose
                // whichever one carries the real signal.
                _log.LogError(
                    "CLI {Binary} exited with code {ExitCode}. stderr={Stderr} stdout={Stdout}",
                    _binary, proc.ExitCode,
                    Truncate(stderr, 2000),
                    Truncate(stdout, 2000));
                var trimmedErr = stderr.Trim();
                if (trimmedErr.Length > 800) trimmedErr = trimmedErr[..800] + "…";
                var trimmedOut = stdout.Trim();
                if (trimmedOut.Length > 400) trimmedOut = trimmedOut[..400] + "…";
                var detail = trimmedErr.Length > 0
                    ? trimmedErr
                    : (trimmedOut.Length > 0 ? $"(no stderr; stdout was: {trimmedOut})" : "(no stderr or stdout)");
                return $"Error: {_binary} exited with code {proc.ExitCode}.\n{detail}";
            }

            if (!string.IsNullOrEmpty(stderr))
                _log.LogDebug("CLI {Binary} stderr (exit 0): {Stderr}", _binary, Truncate(stderr, 1000));

            wallClock.Stop();
            if (_kind == CliKind.Claude)
            {
                var text = ParseClaudeJson(stdout, out var outcome);
                _log.LogInformation(
                    "CLI endpoint done: binary={Binary} job={JobId} exit=0 wallMs={WallMs} stdoutChars={StdoutChars} resultChars={ResultChars} sessionId={SessionId} isError={IsError} apiStatus={ApiStatus} cliDurationMs={CliMs} cliApiMs={CliApiMs} costUsd={Cost} turns={Turns} inTok={InTok} outTok={OutTok} cacheReadTok={CacheReadTok} cacheCreateTok={CacheCreateTok}",
                    _binary, _jobId ?? "(none)",
                    wallClock.ElapsedMilliseconds, stdout.Length, text.Length,
                    outcome.SessionId ?? "(none)", outcome.IsError, outcome.ApiErrorStatus?.ToString() ?? "(none)",
                    outcome.DurationMs?.ToString() ?? "(none)", outcome.DurationApiMs?.ToString() ?? "(none)",
                    outcome.TotalCostUsd?.ToString() ?? "(none)", outcome.NumTurns?.ToString() ?? "(none)",
                    outcome.InputTokens?.ToString() ?? "(none)", outcome.OutputTokens?.ToString() ?? "(none)",
                    outcome.CacheReadTokens?.ToString() ?? "(none)", outcome.CacheCreationTokens?.ToString() ?? "(none)");
                if (outcome.IsError)
                {
                    // Claude can return is_error=true with exit code 0 (e.g. 404 model not found
                    // landed as JSON on stdout). Surface that loudly so it doesn't look like a
                    // silent success when the model never actually answered.
                    _log.LogWarning(
                        "CLI {Binary} job {JobId} returned is_error=true (apiStatus={ApiStatus}, result={Snippet})",
                        _binary, _jobId ?? "(none)", outcome.ApiErrorStatus?.ToString() ?? "(none)", Truncate(text, 400));
                }
                return text;
            }
            else if (_kind == CliKind.Codex)
            {
                var text = ParseCodex(stdout);
                _log.LogInformation(
                    "CLI endpoint done: binary={Binary} job={JobId} exit=0 wallMs={WallMs} stdoutChars={StdoutChars} resultChars={ResultChars}",
                    _binary, _jobId ?? "(none)", wallClock.ElapsedMilliseconds, stdout.Length, text.Length);
                return text;
            }
            else // Copilot
            {
                var text = ParseCopilotJsonl(stdout, out var outcome);
                _log.LogInformation(
                    "CLI endpoint done: binary={Binary} job={JobId} exit=0 wallMs={WallMs} stdoutChars={StdoutChars} resultChars={ResultChars} sessionId={SessionId} events={Events} isError={IsError}",
                    _binary, _jobId ?? "(none)", wallClock.ElapsedMilliseconds, stdout.Length, text.Length,
                    outcome.SessionId ?? "(none)", outcome.EventCount, outcome.IsError);
                if (outcome.IsError)
                {
                    _log.LogWarning(
                        "CLI {Binary} job {JobId} returned an error event (result={Snippet})",
                        _binary, _jobId ?? "(none)", Truncate(text, 400));
                }
                return text;
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    private string ParseClaudeJson(string stdout, out ClaudeOutcome outcome)
    {
        outcome = default;
        var trimmed = stdout.Trim();
        if (trimmed.Length == 0) return "(claude returned no output)";
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return trimmed;
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var msg = err.ValueKind == JsonValueKind.String ? err.GetString() : err.GetRawText();
                outcome.IsError = true;
                return $"Error from claude: {msg}";
            }

            outcome.SessionId = TryReadString(doc.RootElement, "session_id");
            outcome.IsError = TryReadBool(doc.RootElement, "is_error") ?? false;
            outcome.ApiErrorStatus = TryReadInt(doc.RootElement, "api_error_status");
            outcome.DurationMs = TryReadLong(doc.RootElement, "duration_ms");
            outcome.DurationApiMs = TryReadLong(doc.RootElement, "duration_api_ms");
            outcome.TotalCostUsd = TryReadDecimal(doc.RootElement, "total_cost_usd");
            outcome.NumTurns = TryReadInt(doc.RootElement, "num_turns");
            if (doc.RootElement.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                outcome.InputTokens = TryReadLong(usage, "input_tokens");
                outcome.OutputTokens = TryReadLong(usage, "output_tokens");
                outcome.CacheReadTokens = TryReadLong(usage, "cache_read_input_tokens");
                outcome.CacheCreationTokens = TryReadLong(usage, "cache_creation_input_tokens");
            }

            if (_jobId is not null && !string.IsNullOrWhiteSpace(outcome.SessionId))
                _sessions.Set(_jobId, _sessionKey, _cwd, outcome.SessionId);

            if (doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
                return result.GetString() ?? "";
        }
        catch (JsonException) { /* fall through to raw */ }
        return trimmed;
    }

    private static string? TryReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool? TryReadBool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;
    private static int? TryReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    private static long? TryReadLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    private static decimal? TryReadDecimal(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n) ? n : null;

    private struct ClaudeOutcome
    {
        public string? SessionId;
        public bool IsError;
        public int? ApiErrorStatus;
        public long? DurationMs;
        public long? DurationApiMs;
        public decimal? TotalCostUsd;
        public int? NumTurns;
        public long? InputTokens;
        public long? OutputTokens;
        public long? CacheReadTokens;
        public long? CacheCreationTokens;
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
                        _sessions.Set(_jobId, _sessionKey, _cwd, sessionId);
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
                        _sessions.Set(_jobId, _sessionKey, _cwd, sessionId);
                }
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Parses Copilot CLI's <c>--output-format json</c> output. Copilot emits JSONL — one JSON
    /// object per line describing an event in the run. The final assistant answer, session id,
    /// and error status are pulled from whichever events carry them, with a defensive
    /// fall-back to the raw stdout if nothing matches.
    ///
    /// Field names checked here (<c>session_id</c>, <c>role</c>, <c>content</c>, <c>text</c>,
    /// <c>result</c>, <c>error</c>) are the common event-log field names used by Copilot
    /// and other Anthropic/OpenAI-shaped agent CLIs. If the shipped Copilot binary uses
    /// different field names, add them to the extractor rather than rewriting the loop.
    /// </summary>
    private string ParseCopilotJsonl(string stdout, out CopilotOutcome outcome)
    {
        outcome = default;
        var trimmed = stdout.Trim();
        if (trimmed.Length == 0) return "(copilot returned no output)";

        // A run that produced a single non-newline-terminated JSON object still parses cleanly.
        var lines = trimmed.Split('\n');
        var assistantText = new StringBuilder();
        string? lastResult = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            outcome.EventCount++;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                var root = doc.RootElement;

                // Track session id whenever it appears — the final event or a dedicated
                // session-open event both work.
                var sid = TryReadString(root, "session_id") ?? TryReadString(root, "sessionId");
                if (!string.IsNullOrWhiteSpace(sid)) outcome.SessionId = sid;

                // Any event marked as an error surfaces that on the outcome so the caller
                // can log a warning. The error text is used as the reply if no assistant
                // message was produced.
                if (root.TryGetProperty("error", out var errEl))
                {
                    outcome.IsError = true;
                    var msg = errEl.ValueKind == JsonValueKind.String ? errEl.GetString() : errEl.GetRawText();
                    if (!string.IsNullOrEmpty(msg)) lastResult = "Error from copilot: " + msg;
                }
                if (TryReadBool(root, "is_error") == true) outcome.IsError = true;

                // Collect the model's textual reply. Copilot's assistant messages typically
                // arrive as {type:"message", role:"assistant", content:"…"} or the equivalent
                // "text" / "result" variants. Concatenate every assistant chunk we see so
                // multi-message runs don't lose intermediate text.
                var role = TryReadString(root, "role");
                var isAssistant = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase);
                if (isAssistant || TryReadString(root, "type") is "message" or "assistant" or "text")
                {
                    var content = TryReadString(root, "content") ?? TryReadString(root, "text");
                    if (!string.IsNullOrEmpty(content))
                    {
                        if (assistantText.Length > 0) assistantText.Append('\n');
                        assistantText.Append(content);
                    }
                }

                // A dedicated 'result' event, when present, takes precedence over accumulated
                // assistant deltas — Copilot uses it to convey the canonical final answer.
                var result = TryReadString(root, "result");
                if (!string.IsNullOrEmpty(result)) lastResult = result;
            }
            catch (JsonException)
            {
                // A non-JSON line inside the stream is unexpected but non-fatal — skip it,
                // keep parsing the rest, and fall back to raw stdout only if we found nothing.
                continue;
            }
        }

        if (_jobId is not null && !string.IsNullOrWhiteSpace(outcome.SessionId))
            _sessions.Set(_jobId, _sessionKey, _cwd, outcome.SessionId!);

        if (!string.IsNullOrEmpty(lastResult)) return lastResult!;
        if (assistantText.Length > 0) return assistantText.ToString();
        // Nothing structured landed — hand back the raw text so at least a diagnostic surface.
        return trimmed;
    }

    private struct CopilotOutcome
    {
        public string? SessionId;
        public bool IsError;
        public int EventCount;
    }

    private static string FormatArgsForLog(System.Collections.ObjectModel.Collection<string> args)
    {
        // Surface the full launch arg list at Debug, but cap any single arg (the prompt can be
        // huge) so the log line stays readable. Quote args that contain whitespace.
        var sb = new StringBuilder();
        for (var i = 0; i < args.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            var a = Truncate(args[i], 400);
            if (a.Contains(' ') || a.Contains('\t') || a.Length == 0)
            {
                sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
            }
            else sb.Append(a);
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Bounded await over an in-flight ReadToEndAsync task — used on the timeout path so we
    /// can grab whatever Claude managed to write before the kill propagated, without hanging
    /// forever if a pipe didn't actually close.
    /// </summary>
    private static async Task<string> ReadPartialAsync(Task<string> readTask, TimeSpan timeout)
    {
        try
        {
            var done = await Task.WhenAny(readTask, Task.Delay(timeout)).ConfigureAwait(false);
            if (done == readTask) return await readTask.ConfigureAwait(false);
        }
        catch { /* swallow — caller logs an empty string in that case */ }
        return "";
    }
}
