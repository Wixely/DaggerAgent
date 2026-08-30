using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Daggeragent.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daggeragent.Tools;

/// <summary>
/// Tools that delegate a one-shot task to an external CLI agent — currently Claude Code CLI
/// (<c>claude -p … --output-format json</c>) and Codex CLI (<c>codex exec …</c>). The
/// delegated agent runs in its own process, with its own auth/subscription, and a fresh
/// context — DaggerAgent doesn't share its conversation history. MCP servers configured
/// with <see cref="McpServerConfig.PassthroughToCli"/>=true are translated to the CLI's
/// native config format and handed over so the spawned agent gets the same tool surface.
/// </summary>
public sealed class CliDelegationTools
{
    private readonly McpOptions _mcp;
    private readonly ToolsOptions _toolsOptions;
    private readonly HostLaunchInfo _launchInfo;
    private readonly CliSessionStore _sessions;
    private readonly IToolCallSink _sink;
    private readonly ILogger<CliDelegationTools> _log;

    public CliDelegationTools(
        IOptions<McpOptions> mcp,
        IOptions<ToolsOptions> toolsOptions,
        HostLaunchInfo launchInfo,
        CliSessionStore sessions,
        IToolCallSink sink,
        ILogger<CliDelegationTools> log)
    {
        _mcp = mcp.Value;
        _toolsOptions = toolsOptions.Value;
        _launchInfo = launchInfo;
        _sessions = sessions;
        _sink = sink;
        _log = log;
    }

    public IEnumerable<AITool> Build(string? parentJobId, int depth = 0)
    {
        // AllowCliDelegation is the master gate. When off, the tools aren't even registered —
        // a cheap way to keep the tool surface tidy and the agent from trying to use them.
        if (!_toolsOptions.AllowCliDelegation) yield break;

        var jobId = parentJobId ?? "";

        async Task<string> DelegateToClaude(
            [Description("Task or question to delegate. Claude won't see your history — be specific.")] string task,
            [Description("Working directory override. Defaults to DaggerAgent's current working directory.")] string? workingDirectory = null,
            [Description("Start a fresh Claude session even if this job has a prior session id stashed. Default false — successive calls in the same job auto-resume so Claude keeps context across them.")] bool freshSession = false,
            CancellationToken cancellationToken = default)
        {
            // Pull any prior Claude session for THIS job so a follow-up delegation resumes
            // instead of cold-starting. Resume key includes cwd because Claude scopes its
            // session store per project dir; switching dir invalidates the id. Caller can
            // force fresh with freshSession=true.
            var cwd = ResolveCwd(workingDirectory);
            var resumeSession = freshSession ? null : _sessions.Get(jobId, "claude", cwd);
            if (!freshSession && resumeSession is null)
            {
                var staleCwd = _sessions.GetStoredCwd(jobId, "claude");
                if (staleCwd is not null && !string.Equals(staleCwd, cwd, StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogInformation(
                        "delegate_to_claude job={JobId}: dropping resume session (was cwd={OldCwd}, now cwd={NewCwd}) — starting fresh",
                        jobId, staleCwd, cwd);
                    _sessions.Clear(jobId, "claude");
                }
            }
            // Relay the CLI's own tool activity to the sink as child events under this
            // delegation call, so a host watching it (the SSE tool_progress feed) can show
            // "delegate_to_claude ↳ Read foo.cs" while the run is going. The parent call id
            // comes from the invoking client's ambient context — the same source
            // NotifyingAIFunction reads — and is null outside the function loop, in which
            // case the events still attribute by job id alone.
            var relay = new ClaudeStreamRelay(
                _sink, jobId, depth,
                FunctionInvokingChatClient.CurrentContext?.CallContent.CallId);
            try
            {
                return await RunCliAsync(
                    binary: ResolveCliBinary(_toolsOptions.ClaudeCliPath, "claude"),
                    buildArgs: cfgPath =>
                    {
                        // stream-json (NDJSON, one event per line) instead of json, so the
                        // run's tool activity is observable while it happens. The terminal
                        // "result" event carries exactly the fields the json envelope did —
                        // session_id, total_cost_usd, is_error, usage — so session resume
                        // and usage logging are unchanged (verified on claude 2.1.161).
                        // --verbose is required to combine -p with stream-json.
                        var list = new List<string> { "-p", task, "--output-format", "stream-json", "--verbose", "--mcp-config", cfgPath };
                        if (!string.IsNullOrWhiteSpace(resumeSession))
                        {
                            list.Add("--resume");
                            list.Add(resumeSession);
                        }
                        return list;
                    },
                    buildConfig: CliMcpConfigBuilder.BuildClaudeConfig,
                    configFileName: "claude-mcp.json",
                    envVarsOverride: null,
                    parseStdout: stdout => ParseClaudeStreamJsonResultAndStashSession(stdout, jobId, cwd),
                    workingDirectory: workingDirectory,
                    onStdoutLine: relay.OnLine,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // A killed or crashed run never sends tool_result for in-flight calls; close
                // them so a live tracker doesn't show them running forever.
                relay.CloseOpenCalls();
            }
        }

        async Task<string> DelegateToCodex(
            [Description("Task or question to delegate. Codex won't see your history — be specific.")] string task,
            [Description("Working directory override. Defaults to DaggerAgent's current working directory.")] string? workingDirectory = null,
            [Description("Start a fresh Codex session even if this job has a prior session id stashed. Default false.")] bool freshSession = false,
            CancellationToken cancellationToken = default)
        {
            var cwd = ResolveCwd(workingDirectory);
            var resumeSession = freshSession ? null : _sessions.Get(jobId, "codex", cwd);
            if (!freshSession && resumeSession is null)
            {
                var staleCwd = _sessions.GetStoredCwd(jobId, "codex");
                if (staleCwd is not null && !string.Equals(staleCwd, cwd, StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogInformation(
                        "delegate_to_codex job={JobId}: dropping resume session (was cwd={OldCwd}, now cwd={NewCwd}) — starting fresh",
                        jobId, staleCwd, cwd);
                    _sessions.Clear(jobId, "codex");
                }
            }
            // Codex picks its config from CODEX_HOME/config.toml — point it at our temp dir.
            return await RunCliAsync(
                binary: ResolveCliBinary(_toolsOptions.CodexCliPath, "codex"),
                buildArgs: _ =>
                {
                    var list = new List<string>();
                    // Codex resumes via `codex exec resume <sessionId>` subcommand.
                    if (!string.IsNullOrWhiteSpace(resumeSession))
                    {
                        list.Add("exec");
                        list.Add("resume");
                        list.Add(resumeSession);
                        list.Add(task);
                    }
                    else
                    {
                        list.Add("exec");
                        list.Add(task);
                    }
                    return list;
                },
                buildConfig: CliMcpConfigBuilder.BuildCodexConfig,
                configFileName: "config.toml",
                envVarsOverride: tmpDir => new Dictionary<string, string> { ["CODEX_HOME"] = tmpDir },
                parseStdout: stdout => ParseCodexResultAndStashSession(stdout, jobId, cwd),
                workingDirectory: workingDirectory,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        async Task<string> DelegateToCopilot(
            [Description("Task or question to delegate. Copilot won't see your history — be specific.")] string task,
            [Description("Working directory override. Defaults to DaggerAgent's current working directory.")] string? workingDirectory = null,
            [Description("Start a fresh Copilot session even if this job has a prior session id stashed. Default false.")] bool freshSession = false,
            CancellationToken cancellationToken = default)
        {
            var cwd = ResolveCwd(workingDirectory);
            var resumeSession = freshSession ? null : _sessions.Get(jobId, "copilot", cwd);
            if (!freshSession && resumeSession is null)
            {
                var staleCwd = _sessions.GetStoredCwd(jobId, "copilot");
                if (staleCwd is not null && !string.Equals(staleCwd, cwd, StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogInformation(
                        "delegate_to_copilot job={JobId}: dropping resume session (was cwd={OldCwd}, now cwd={NewCwd}) — starting fresh",
                        jobId, staleCwd, cwd);
                    _sessions.Clear(jobId, "copilot");
                }
            }
            return await RunCliAsync(
                binary: ResolveCliBinary(_toolsOptions.CopilotCliPath, "copilot"),
                buildArgs: cfgPath =>
                {
                    // --allow-all-tools + --no-ask-user are mandatory for non-interactive
                    // mode: without them Copilot either refuses to start ("required for
                    // non-interactive mode") or spawns interactive prompts that deadlock a
                    // subprocess with no TTY.
                    var list = new List<string>
                    {
                        "-p", task,
                        "--output-format", "json",
                        "--silent",
                        "--allow-all-tools",
                        "--no-ask-user",
                        "--additional-mcp-config", "@" + cfgPath,
                    };
                    if (!string.IsNullOrWhiteSpace(resumeSession))
                    {
                        list.Add("--session-id");
                        list.Add(resumeSession);
                    }
                    return list;
                },
                buildConfig: CliMcpConfigBuilder.BuildCopilotConfig,
                configFileName: "copilot-mcp.json",
                // No COPILOT_HOME override: the subprocess must inherit the user's auth from
                // ~/.copilot/auth. See CliChatClient's Copilot branch for the same rationale.
                envVarsOverride: null,
                parseStdout: stdout => ParseCopilotJsonlAndStashSession(stdout, jobId, cwd),
                workingDirectory: workingDirectory,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        yield return AIFunctionFactory.Create(DelegateToClaude, name: "delegate_to_claude", description:
            "Delegate a task to the Claude Code CLI as a one-shot subprocess. Returns Claude's final " +
            "answer text. The delegated agent has a fresh context with no access to DaggerAgent's " +
            "conversation — pass enough detail in `task` to make it actionable. MCP servers configured " +
            "with PassthroughToCli=true are made available to Claude with the same auth.");

        yield return AIFunctionFactory.Create(DelegateToCodex, name: "delegate_to_codex", description:
            "Delegate a task to the Codex CLI as a one-shot subprocess. Returns Codex's final " +
            "assistant message. The delegated agent has a fresh context with no access to DaggerAgent's " +
            "conversation — pass enough detail in `task` to make it actionable. Stdio MCP servers configured " +
            "with PassthroughToCli=true are made available to Codex; HTTP servers are skipped (Codex CLI " +
            "config doesn't currently support HTTP MCP transport).");

        yield return AIFunctionFactory.Create(DelegateToCopilot, name: "delegate_to_copilot", description:
            "Delegate a task to the GitHub Copilot CLI as a one-shot subprocess. Returns Copilot's final " +
            "answer text. The delegated agent has a fresh context with no access to DaggerAgent's " +
            "conversation — pass enough detail in `task` to make it actionable. MCP servers configured " +
            "with PassthroughToCli=true are made available to Copilot (both HTTP and stdio transports " +
            "are supported, unlike Codex). Sessions are per-job + per-cwd — successive calls in the " +
            "same working directory auto-resume; pass freshSession=true to start over.");
    }

    private static string ResolveCliBinary(string? configured, string fallbackName) =>
        string.IsNullOrWhiteSpace(configured) ? fallbackName : configured.Trim();

    /// <summary>
    /// Mirrors the cwd-resolution logic in <see cref="RunCliAsync"/> so the caller can compute
    /// the same path before invoking — needed to pin Claude/Codex session ids to a cwd, since
    /// those CLIs scope sessions per project directory.
    /// </summary>
    private string ResolveCwd(string? workingDirectoryOverride)
    {
        // Precedence: explicit tool override → the current turn's ambient cwd (ToolExecutionContext,
        // set per request so concurrent jobs don't clobber a shared global) → the sticky ToolsOptions
        // default → the launch cwd.
        var configured = ToolExecutionContext.WorkingDirectory ?? _toolsOptions.WorkingDirectory;
        return !string.IsNullOrWhiteSpace(workingDirectoryOverride)
            ? workingDirectoryOverride!
            : (!string.IsNullOrWhiteSpace(configured) ? configured : _launchInfo.OriginalWorkingDirectory);
    }

    private static string FormatArgsForLog(System.Collections.ObjectModel.Collection<string> args)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < args.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            var a = Truncate(args[i], 400);
            if (a.Contains(' ') || a.Contains('\t') || a.Length == 0)
                sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
            else sb.Append(a);
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private async Task<string> RunCliAsync(
        string binary,
        Func<string, IEnumerable<string>> buildArgs,
        Func<IEnumerable<McpServerConfig>, string> buildConfig,
        string configFileName,
        Func<string, IDictionary<string, string>>? envVarsOverride,
        Func<string, string> parseStdout,
        string? workingDirectory,
        CancellationToken cancellationToken,
        Action<string>? onStdoutLine = null)
    {
        var cwd = ResolveCwd(workingDirectory);

        var passthrough = _mcp.Servers.Where(s => s.Enabled && s.PassthroughToCli).ToList();

        // Per-invocation temp dir holds the generated MCP config; cleaned up in the finally so
        // we don't leave behind API tokens in env-var form on disk.
        var tempDir = Path.Combine(Path.GetTempPath(),
            "dagger-cli-" + Guid.NewGuid().ToString("N").Substring(0, 16));
        Directory.CreateDirectory(tempDir);
        var wallClock = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var configPath = Path.Combine(tempDir, configFileName);
            await File.WriteAllTextAsync(configPath, buildConfig(passthrough), cancellationToken).ConfigureAwait(false);

            var psi = new ProcessStartInfo
            {
                FileName = binary,
                WorkingDirectory = cwd,
                // Redirect stdin so we can close it immediately — Claude Code CLI inspects
                // stdin and waits ~3s for piped input when it's a non-TTY handle (which an
                // inherited server stdin is), then exits with code 1. Closing signals EOF.
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in buildArgs(configPath)) psi.ArgumentList.Add(arg);
            if (envVarsOverride is not null)
            {
                foreach (var (k, v) in envVarsOverride(tempDir))
                    psi.Environment[k] = v;
            }

            _log.LogInformation(
                "Delegating to {Binary} (cwd={Cwd}, passthroughServers={Count})",
                binary, cwd, passthrough.Count);
            _log.LogDebug(
                "CLI delegation args: {Args}",
                FormatArgsForLog(psi.ArgumentList));

            Process proc;
            try { proc = Process.Start(psi)!; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
            {
                _log.LogError(ex, "Failed to start CLI binary {Binary}", binary);
                return $"Error: failed to start '{binary}' — is it installed and on PATH? ({ex.Message})";
            }

            try { proc.StandardInput.Close(); }
            catch (Exception ex) { _log.LogDebug(ex, "Closing stdin for {Binary} failed (likely already closed)", binary); }

            // Bound the run. Until now a delegation was limited only by the parent agent's
            // token, so a wedged CLI could hang the turn indefinitely.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timeout = TimeSpan.FromSeconds(_toolsOptions.CliDelegationTimeoutSeconds);
            timeoutCts.CancelAfter(timeout);

            // If the parent agent is cancelled mid-call, kill the CLI so it doesn't outlive us.
            await using var killReg = timeoutCts.Token.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch { /* race with natural exit — ignore */ }
            });

            // No cancellation token on the reads: the kill above closes the pipes, ending these
            // naturally with whatever the CLI wrote. See ProcessOutput for why passing the token
            // instead throws that output away. Buffered read by default; line-streaming when a
            // caller observes the run as it happens (Claude's stream-json) — either way the
            // task resolves to the full stdout, so the timeout path's partial-output salvage
            // works the same.
            var stdoutTask = onStdoutLine is null
                ? proc.StandardOutput.ReadToEndAsync()
                : ReadLinesAsync(proc.StandardOutput, onStdoutLine);
            var stderrTask = proc.StandardError.ReadToEndAsync();

            try { await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                wallClock.Stop();
                var partialOut = (await ProcessOutput.ReadPartialAsync(stdoutTask).ConfigureAwait(false)).Trim();
                var partialErr = (await ProcessOutput.ReadPartialAsync(stderrTask).ConfigureAwait(false)).Trim();
                var cancelledByCaller = cancellationToken.IsCancellationRequested;
                _log.LogWarning(
                    "CLI delegation {Outcome}: binary={Binary} wallMs={WallMs} timeoutSec={TimeoutSec} partialStdoutChars={StdoutChars} partialStderrChars={StderrChars}",
                    cancelledByCaller ? "cancelled" : "timed out",
                    binary, wallClock.ElapsedMilliseconds, timeout.TotalSeconds,
                    partialOut.Length, partialErr.Length);

                // Hand back whatever it produced. A delegated run is expensive and slow; losing
                // several minutes of work to an error string is the worst possible outcome.
                var bits = new List<string>();
                if (partialErr.Length > 0) bits.Add($"stderr: {Truncate(partialErr, 600)}");
                if (partialOut.Length > 0) bits.Add($"stdout: {Truncate(partialOut, 600)}");
                if (bits.Count == 0) bits.Add("(no output captured before the kill)");
                var reason = cancelledByCaller
                    ? $"'{binary}' delegation cancelled."
                    : $"'{binary}' delegation timed out after {timeout.TotalSeconds:F0}s and was killed.";
                return $"Error: {reason}\n{string.Join("\n", bits)}";
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                _log.LogError(
                    "CLI {Binary} exited with code {ExitCode}. stderr={Stderr} stdout={Stdout}",
                    binary, proc.ExitCode,
                    Truncate(stderr, 2000),
                    Truncate(stdout, 2000));
                var trimmedErr = stderr.Trim();
                if (trimmedErr.Length > 800) trimmedErr = trimmedErr[..800] + "…";
                var trimmedOut = stdout.Trim();
                if (trimmedOut.Length > 400) trimmedOut = trimmedOut[..400] + "…";
                var detail = trimmedErr.Length > 0
                    ? trimmedErr
                    : (trimmedOut.Length > 0 ? $"(no stderr; stdout was: {trimmedOut})" : "(no stderr or stdout)");
                return $"Error: {binary} exited with code {proc.ExitCode}.\n{detail}";
            }

            if (!string.IsNullOrEmpty(stderr))
                _log.LogDebug("CLI {Binary} stderr (exit 0): {Stderr}", binary, Truncate(stderr, 1000));

            wallClock.Stop();
            var result = parseStdout(stdout);
            // For Claude in --output-format json the parser already extracted session_id and
            // any error meta, but we still want a high-level INFO completion line so the user
            // can see "did it land" without flipping logging to Debug. Snippet of the result
            // helps distinguish a real answer from a "(claude returned no output)" placeholder.
            _log.LogInformation(
                "CLI delegation done: binary={Binary} exit=0 wallMs={WallMs} stdoutChars={StdoutChars} resultChars={ResultChars} resultSnippet={Snippet}",
                binary, wallClock.ElapsedMilliseconds, stdout.Length, result.Length, Truncate(result, 240));
            return result;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best-effort; OS will eventually GC temp */ }
        }
    }

    /// <summary>
    /// Drains a pipe line-by-line, invoking <paramref name="onLine"/> per line, and resolves to
    /// the full text once the pipe closes. An observer that throws must not kill the delegation
    /// it is merely watching, so its exceptions are swallowed; the observer does its own logging.
    /// </summary>
    private static async Task<string> ReadLinesAsync(System.IO.StreamReader reader, Action<string> onLine)
    {
        var sb = new StringBuilder();
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            sb.Append(line).Append('\n');
            try { onLine(line); } catch { /* observer-only */ }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parses <c>--output-format stream-json</c> output: finds the terminal <c>result</c> event —
    /// the same envelope <c>--output-format json</c> returned — and hands that one line to the
    /// original parser, so session stashing, usage logging and the meta line are unchanged. A
    /// stream with no result event means the run was killed or crashed mid-way; the assistant
    /// text seen so far is salvaged rather than dumping raw NDJSON at the model.
    /// </summary>
    private string ParseClaudeStreamJsonResultAndStashSession(string stdout, string jobId, string cwd)
    {
        var lines = stdout.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("type", out var t)
                    && t.ValueKind == JsonValueKind.String
                    && t.GetString() == "result")
                {
                    return ParseClaudeJsonResultAndStashSession(line, jobId, cwd);
                }
            }
            catch (JsonException) { /* diagnostics interleaved with events — keep scanning */ }
        }

        var salvaged = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out var t) || t.GetString() != "assistant"
                    || !root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object
                    || !msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var item in content.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("type", out var it) && it.GetString() == "text"
                        && item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        salvaged.Append(text.GetString());
                    }
                }
            }
            catch (JsonException) { }
        }
        return salvaged.Length > 0
            ? salvaged + "\n\n[claude stream ended without a result event — output may be incomplete]"
            : "(claude returned no output)";
    }

    /// <summary>
    /// Translates the Claude CLI's stream-json events into child <see cref="ToolCallEvent"/>s
    /// under the enclosing <c>delegate_to_claude</c> call, so a host watching the sink sees
    /// which tool the delegated run is on. Observability only — the model still receives the
    /// final aggregate. Shape verified against claude 2.1.161: an <c>assistant</c> event
    /// carries <c>tool_use</c> content items <c>{id, name, input}</c>; a <c>user</c> event
    /// carries <c>tool_result</c> items <c>{tool_use_id, content, is_error}</c>. Elapsed is
    /// measured between the two events arriving here — close enough for display, which is all
    /// these events are for. Same privacy stance as <see cref="ToolCallSink.DigestArgs"/>: the
    /// digest carries argument names, never values.
    /// </summary>
    private sealed class ClaudeStreamRelay
    {
        private readonly IToolCallSink _sink;
        private readonly string _jobId;
        private readonly int _depth;
        private readonly string? _parentCallId;
        private readonly Dictionary<string, (ToolCallEvent Started, long StartedAt)> _open = new(StringComparer.Ordinal);

        public ClaudeStreamRelay(IToolCallSink sink, string jobId, int depth, string? parentCallId)
        {
            _sink = sink;
            _jobId = jobId;
            _depth = depth;
            _parentCallId = parentCallId;
        }

        public void OnLine(string raw)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '{') return;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String)
                {
                    return;
                }
                var type = t.GetString();
                if (type is not ("assistant" or "user")) return;
                if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object
                    || !msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                {
                    return;
                }
                foreach (var item in content.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !item.TryGetProperty("type", out var it) || it.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }
                    if (type == "assistant" && it.GetString() == "tool_use") OnToolUse(item);
                    else if (type == "user" && it.GetString() == "tool_result") OnToolResult(item);
                }
            }
            catch (JsonException) { /* claude interleaves diagnostics with events on stdout */ }
        }

        private void OnToolUse(JsonElement item)
        {
            if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return;
            if (!item.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) return;
            var id = idEl.GetString()!;
            if (_open.ContainsKey(id)) return;
            var argNames = item.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object
                ? string.Join(",", input.EnumerateObject().Select(p => p.Name))
                : "";
            var started = new ToolCallEvent(_jobId, _depth, nameEl.GetString()!, argNames.Length > 0 ? argNames : "(none)")
            {
                CallId = id,
                ParentCallId = _parentCallId,
            };
            _open[id] = (started, Stopwatch.GetTimestamp());
            _sink.Started(started);
        }

        private void OnToolResult(JsonElement item)
        {
            if (!item.TryGetProperty("tool_use_id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return;
            if (!_open.Remove(idEl.GetString()!, out var entry)) return;
            var isError = item.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True;
            var resultChars = item.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()!.Length
                : 0;
            _sink.Completed(entry.Started with
            {
                Elapsed = Stopwatch.GetElapsedTime(entry.StartedAt),
                Succeeded = !isError,
                ResultChars = resultChars,
                Error = isError ? "CliToolError" : null,
            });
        }

        public void CloseOpenCalls()
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
        }
    }

    private string ParseClaudeJsonResultAndStashSession(string stdout, string jobId, string cwd)
    {
        // Claude --output-format json returns a single object: {result, session_id, total_cost_usd, usage}.
        // Pull just .result if present, capture session_id (tagged with the cwd it was created in
        // so a later call from a different cwd doesn't try to resume) into the per-job store for
        // the next call's --resume, and hand a trailing meta line back so the model can see/use the id.
        var trimmed = stdout.Trim();
        if (trimmed.Length == 0) return "(claude returned no output)";
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("error", out var err))
                {
                    var msg = err.ValueKind == JsonValueKind.String ? err.GetString() : err.GetRawText();
                    return $"Error from claude: {msg}";
                }
                var meta = new StringBuilder();
                string? sessionId = null;
                if (doc.RootElement.TryGetProperty("session_id", out var sid))
                {
                    sessionId = sid.GetString();
                    if (!string.IsNullOrWhiteSpace(sessionId))
                    {
                        _sessions.Set(jobId, "claude", cwd, sessionId);
                        meta.Append("session_id=").Append(sessionId);
                    }
                }
                if (doc.RootElement.TryGetProperty("total_cost_usd", out var cost))
                {
                    if (meta.Length > 0) meta.Append("  ");
                    meta.Append("cost_usd=").Append(cost.GetRawText());
                }
                // is_error=true on a process-exit-0 JSON means Claude itself rejected the
                // request (e.g. model 404). Log a warning so the agent / caller sees it as
                // a real failure rather than a quiet success.
                if (doc.RootElement.TryGetProperty("is_error", out var isErr)
                    && (isErr.ValueKind == JsonValueKind.True || isErr.ValueKind == JsonValueKind.False)
                    && isErr.GetBoolean())
                {
                    var apiStatus = doc.RootElement.TryGetProperty("api_error_status", out var s) && s.ValueKind == JsonValueKind.Number
                        ? s.GetRawText() : "(none)";
                    _log.LogWarning(
                        "delegate_to_claude: Claude returned is_error=true (apiStatus={ApiStatus}, sessionId={SessionId})",
                        apiStatus, sessionId ?? "(none)");
                }
                // Log the delegated (subscription-billed) usage for observability. Deliberately NOT
                // added to the job's endpoint cost/token totals: that would mix a subscription
                // subprocess's notional cost and a different model's tokens into the per-endpoint API
                // accounting. This makes "what did the delegated agent burn" answerable from telemetry.
                LogClaudeDelegatedUsage(doc.RootElement, jobId, sessionId);
                if (doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
                {
                    var body = result.GetString() ?? "";
                    return meta.Length > 0 ? body + "\n\n[" + meta + " — next call auto-resumes this session; pass freshSession=true to start over]" : body;
                }
            }
        }
        catch (JsonException) { /* fall through to raw */ }
        return trimmed;
    }

    /// <summary>
    /// Emit an INFO line with the delegated Claude CLI's reported usage (cost + token counts from
    /// its <c>--output-format json</c> envelope). Observability only — see the call site for why
    /// this is not folded into the job's endpoint accounting.
    /// </summary>
    private void LogClaudeDelegatedUsage(JsonElement root, string jobId, string? sessionId)
    {
        decimal? costUsd = root.TryGetProperty("total_cost_usd", out var c)
            && c.ValueKind == JsonValueKind.Number && c.TryGetDecimal(out var cd) ? cd : null;
        long? inTok = null, outTok = null, cacheRead = null, cacheCreate = null;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            inTok = TryReadLong(usage, "input_tokens");
            outTok = TryReadLong(usage, "output_tokens");
            cacheRead = TryReadLong(usage, "cache_read_input_tokens");
            cacheCreate = TryReadLong(usage, "cache_creation_input_tokens");
        }
        int? numTurns = root.TryGetProperty("num_turns", out var nt)
            && nt.ValueKind == JsonValueKind.Number && nt.TryGetInt32(out var ntv) ? ntv : null;
        if (costUsd is null && inTok is null && outTok is null) return;  // nothing worth a line
        _log.LogInformation(
            "Delegated Claude usage (subscription-billed, NOT in job totals): job={JobId} session={Session} " +
            "costUsd={Cost} inputTokens={In} outputTokens={Out} cacheReadTokens={CacheRead} cacheCreationTokens={CacheCreate} numTurns={Turns}",
            jobId, sessionId ?? "(none)", costUsd, inTok, outTok, cacheRead, cacheCreate, numTurns);
    }

    /// <summary>
    /// Parses Copilot CLI's <c>--output-format json</c> (JSONL) output. Schema verified against
    /// Copilot CLI 1.0.67 — each event is <c>{ type, data: {…}, id, timestamp, parentId }</c>
    /// except the terminal <c>type:"result"</c> event which puts <c>sessionId</c>, <c>exitCode</c>
    /// and <c>usage</c> at the top level. See <see cref="CliChatClient"/>.ParseCopilotJsonl for
    /// the mirror implementation on the endpoint path — keep the two in sync when the schema
    /// changes.
    /// </summary>
    private string ParseCopilotJsonlAndStashSession(string stdout, string jobId, string cwd)
    {
        var trimmed = stdout.Trim();
        if (trimmed.Length == 0) return "(copilot returned no output)";

        var assistantMessage = new StringBuilder();
        var assistantDeltas = new StringBuilder();
        var warnings = new StringBuilder();
        string? sessionId = null;
        int? exitCode = null;
        int? premiumRequests = null;
        long outputTokens = 0;
        int events = 0;

        foreach (var raw in trimmed.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            events++;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                var root = doc.RootElement;
                var type = TryReadStringField(root, "type");
                if (type is null) continue;

                switch (type)
                {
                    case "result":
                        sessionId = TryReadStringField(root, "sessionId") ?? sessionId;
                        if (root.TryGetProperty("exitCode", out var ecEl) && ecEl.ValueKind == JsonValueKind.Number && ecEl.TryGetInt32(out var ec))
                            exitCode = ec;
                        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                            && usage.TryGetProperty("premiumRequests", out var pr) && pr.ValueKind == JsonValueKind.Number && pr.TryGetInt32(out var prv))
                            premiumRequests = prv;
                        break;

                    case "assistant.message":
                        if (root.TryGetProperty("data", out var msgData) && msgData.ValueKind == JsonValueKind.Object)
                        {
                            var content = TryReadStringField(msgData, "content");
                            if (!string.IsNullOrEmpty(content))
                            {
                                if (assistantMessage.Length > 0) assistantMessage.Append('\n');
                                assistantMessage.Append(content);
                            }
                            outputTokens += TryReadLong(msgData, "outputTokens") ?? 0;
                        }
                        break;

                    case "assistant.message_delta":
                        if (root.TryGetProperty("data", out var deltaData) && deltaData.ValueKind == JsonValueKind.Object)
                        {
                            var delta = TryReadStringField(deltaData, "deltaContent");
                            if (!string.IsNullOrEmpty(delta)) assistantDeltas.Append(delta);
                        }
                        break;

                    case "session.warning":
                        if (root.TryGetProperty("data", out var warnData) && warnData.ValueKind == JsonValueKind.Object)
                        {
                            var msg = TryReadStringField(warnData, "message");
                            if (!string.IsNullOrEmpty(msg))
                            {
                                if (warnings.Length > 0) warnings.AppendLine();
                                warnings.Append("[copilot warning] ").Append(msg);
                            }
                        }
                        break;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
            _sessions.Set(jobId, "copilot", cwd, sessionId!);

        if (warnings.Length > 0)
            _log.LogWarning("delegate_to_copilot session warnings (sessionId={SessionId}): {Warnings}",
                sessionId ?? "(none)", warnings.ToString());

        // Prefer the canonical assistant.message content, then streamed deltas, then warnings.
        string body;
        if (assistantMessage.Length > 0) body = assistantMessage.ToString();
        else if (assistantDeltas.Length > 0) body = assistantDeltas.ToString();
        else if (warnings.Length > 0) body = warnings.ToString();
        else body = trimmed;

        if (exitCode is int ec2 && ec2 != 0)
        {
            _log.LogWarning(
                "delegate_to_copilot: Copilot exited with code {ExitCode} (sessionId={SessionId}, events={Events})",
                ec2, sessionId ?? "(none)", events);
        }

        // Log the delegated (subscription-billed) usage for observability — same rationale as the
        // Claude path: recorded in telemetry, not folded into the job's endpoint cost/token totals.
        if (premiumRequests is not null || outputTokens > 0)
            _log.LogInformation(
                "Delegated Copilot usage (subscription-billed, NOT in job totals): job={JobId} session={Session} " +
                "premiumRequests={Premium} outputTokens={Out} events={Events}",
                jobId, sessionId ?? "(none)", premiumRequests, outputTokens, events);

        var meta = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(sessionId))
            meta.Append("session_id=").Append(sessionId);
        if (premiumRequests is int pr2)
        {
            if (meta.Length > 0) meta.Append("  ");
            meta.Append("premium_requests=").Append(pr2);
        }
        return meta.Length > 0
            ? body + "\n\n[" + meta + " — next call auto-resumes this session; pass freshSession=true to start over]"
            : body;
    }

    private static string? TryReadStringField(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? TryReadLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    private string ParseCodexResultAndStashSession(string stdout, string jobId, string cwd)
    {
        // Codex `exec` prints the final assistant message to stdout. When the user passes --json
        // (some versions only) the last line is a JSON object with session info. We try to detect
        // a session id either way: look for a final line shaped like `Session: <id>` or a JSON tail.
        // Stash the id tagged with the cwd it was created in so a later call from a different cwd
        // doesn't try to resume against a project Codex never associated with this directory.
        var trimmed = stdout.TrimEnd();
        if (trimmed.Length == 0) return "(codex returned no output)";

        // Heuristic 1: trailing JSON object.
        var lastNewline = trimmed.LastIndexOf('\n');
        var lastLine = lastNewline >= 0 ? trimmed[(lastNewline + 1)..] : trimmed;
        if (lastLine.StartsWith("{") && lastLine.EndsWith("}"))
        {
            try
            {
                using var doc = JsonDocument.Parse(lastLine);
                if (doc.RootElement.TryGetProperty("session_id", out var sid))
                {
                    var sessionId = sid.GetString();
                    if (!string.IsNullOrWhiteSpace(sessionId))
                        _sessions.Set(jobId, "codex", cwd, sessionId);
                }
            }
            catch (JsonException) { /* ignore */ }
        }

        // Heuristic 2: `Session: <uuid>` line anywhere in the output.
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
                    _sessions.Set(jobId, "codex", cwd, sessionId);
            }
        }

        return trimmed;
    }
}
