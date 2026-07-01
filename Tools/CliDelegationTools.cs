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
    private readonly ILogger<CliDelegationTools> _log;

    public CliDelegationTools(
        IOptions<McpOptions> mcp,
        IOptions<ToolsOptions> toolsOptions,
        HostLaunchInfo launchInfo,
        CliSessionStore sessions,
        ILogger<CliDelegationTools> log)
    {
        _mcp = mcp.Value;
        _toolsOptions = toolsOptions.Value;
        _launchInfo = launchInfo;
        _sessions = sessions;
        _log = log;
    }

    public IEnumerable<AITool> Build(string? parentJobId)
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
            return await RunCliAsync(
                binary: ResolveCliBinary(_toolsOptions.ClaudeCliPath, "claude"),
                buildArgs: cfgPath =>
                {
                    var list = new List<string> { "-p", task, "--output-format", "json", "--mcp-config", cfgPath };
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
                parseStdout: stdout => ParseClaudeJsonResultAndStashSession(stdout, jobId, cwd),
                workingDirectory: workingDirectory,
                cancellationToken: cancellationToken).ConfigureAwait(false);
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
                    var list = new List<string>
                    {
                        "-p", task,
                        "--output-format", "json",
                        "--silent",
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
                envVarsOverride: tmpDir => new Dictionary<string, string> { ["COPILOT_HOME"] = tmpDir },
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
    private string ResolveCwd(string? workingDirectoryOverride) =>
        !string.IsNullOrWhiteSpace(workingDirectoryOverride)
            ? workingDirectoryOverride!
            : (!string.IsNullOrWhiteSpace(_toolsOptions.WorkingDirectory)
                ? _toolsOptions.WorkingDirectory
                : _launchInfo.OriginalWorkingDirectory);

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
        CancellationToken cancellationToken)
    {
        var cwd = !string.IsNullOrWhiteSpace(workingDirectory)
            ? workingDirectory!
            : (!string.IsNullOrWhiteSpace(_toolsOptions.WorkingDirectory)
                ? _toolsOptions.WorkingDirectory
                : _launchInfo.OriginalWorkingDirectory);

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

            // If the parent agent is cancelled mid-call, kill the CLI so it doesn't outlive us.
            await using var killReg = cancellationToken.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch { /* race with natural exit — ignore */ }
            });

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);

            try { await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                return $"Error: '{binary}' delegation cancelled.";
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
    /// Parses Copilot CLI's <c>--output-format json</c> (JSONL) output. Each line is a JSON
    /// object describing an event; the final assistant answer and session id are pulled from
    /// whichever events carry them. Session id is stashed in the per-job store keyed by cwd so
    /// a follow-up delegation auto-resumes; a trailing meta line surfaces the id + any cost
    /// info back to the calling model.
    /// </summary>
    private string ParseCopilotJsonlAndStashSession(string stdout, string jobId, string cwd)
    {
        var trimmed = stdout.Trim();
        if (trimmed.Length == 0) return "(copilot returned no output)";

        var assistantText = new StringBuilder();
        string? lastResult = null;
        string? sessionId = null;
        string? errorMsg = null;
        int events = 0;

        foreach (var raw in trimmed.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            events++;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                var root = doc.RootElement;

                var sid = TryReadStringField(root, "session_id") ?? TryReadStringField(root, "sessionId");
                if (!string.IsNullOrWhiteSpace(sid)) sessionId = sid;

                if (root.TryGetProperty("error", out var errEl))
                {
                    var msg = errEl.ValueKind == JsonValueKind.String ? errEl.GetString() : errEl.GetRawText();
                    if (!string.IsNullOrEmpty(msg)) errorMsg = msg;
                }

                var role = TryReadStringField(root, "role");
                var type = TryReadStringField(root, "type");
                var isAssistant = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                    || type is "message" or "assistant" or "text";
                if (isAssistant)
                {
                    var content = TryReadStringField(root, "content") ?? TryReadStringField(root, "text");
                    if (!string.IsNullOrEmpty(content))
                    {
                        if (assistantText.Length > 0) assistantText.Append('\n');
                        assistantText.Append(content);
                    }
                }

                var result = TryReadStringField(root, "result");
                if (!string.IsNullOrEmpty(result)) lastResult = result;
            }
            catch (JsonException) { /* skip malformed line */ }
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
            _sessions.Set(jobId, "copilot", cwd, sessionId!);

        if (errorMsg is not null)
        {
            _log.LogWarning(
                "delegate_to_copilot: Copilot returned an error event (sessionId={SessionId}, events={Events})",
                sessionId ?? "(none)", events);
            return "Error from copilot: " + errorMsg;
        }

        var body = lastResult ?? (assistantText.Length > 0 ? assistantText.ToString() : trimmed);
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            return body
                + "\n\n[session_id=" + sessionId
                + " — next call auto-resumes this session; pass freshSession=true to start over]";
        }
        return body;
    }

    private static string? TryReadStringField(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

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
