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
            // instead of cold-starting. Caller can force fresh with freshSession=true.
            var resumeSession = freshSession ? null : _sessions.Get(jobId, "claude");
            return await RunCliAsync(
                binary: "claude",
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
                parseStdout: stdout => ParseClaudeJsonResultAndStashSession(stdout, jobId),
                workingDirectory: workingDirectory,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        async Task<string> DelegateToCodex(
            [Description("Task or question to delegate. Codex won't see your history — be specific.")] string task,
            [Description("Working directory override. Defaults to DaggerAgent's current working directory.")] string? workingDirectory = null,
            [Description("Start a fresh Codex session even if this job has a prior session id stashed. Default false.")] bool freshSession = false,
            CancellationToken cancellationToken = default)
        {
            var resumeSession = freshSession ? null : _sessions.Get(jobId, "codex");
            // Codex picks its config from CODEX_HOME/config.toml — point it at our temp dir.
            return await RunCliAsync(
                binary: "codex",
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
                parseStdout: stdout => ParseCodexResultAndStashSession(stdout, jobId),
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
    }

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

        try
        {
            var configPath = Path.Combine(tempDir, configFileName);
            await File.WriteAllTextAsync(configPath, buildConfig(passthrough), cancellationToken).ConfigureAwait(false);

            var psi = new ProcessStartInfo
            {
                FileName = binary,
                WorkingDirectory = cwd,
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

            Process proc;
            try { proc = Process.Start(psi)!; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
            {
                return $"Error: failed to start '{binary}' — is it installed and on PATH? ({ex.Message})";
            }

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
                var trimmedErr = stderr.Trim();
                if (trimmedErr.Length > 800) trimmedErr = trimmedErr[..800] + "…";
                return $"Error: {binary} exited with code {proc.ExitCode}.\n{trimmedErr}";
            }

            return parseStdout(stdout);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best-effort; OS will eventually GC temp */ }
        }
    }

    private string ParseClaudeJsonResultAndStashSession(string stdout, string jobId)
    {
        // Claude --output-format json returns a single object: {result, session_id, total_cost_usd, usage}.
        // Pull just .result if present, capture session_id into the per-job store for the next
        // call's --resume, and hand a trailing meta line back so the model can see/use the id.
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
                        _sessions.Set(jobId, "claude", sessionId);
                        meta.Append("session_id=").Append(sessionId);
                    }
                }
                if (doc.RootElement.TryGetProperty("total_cost_usd", out var cost))
                {
                    if (meta.Length > 0) meta.Append("  ");
                    meta.Append("cost_usd=").Append(cost.GetRawText());
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

    private string ParseCodexResultAndStashSession(string stdout, string jobId)
    {
        // Codex `exec` prints the final assistant message to stdout. When the user passes --json
        // (some versions only) the last line is a JSON object with session info. We try to detect
        // a session id either way: look for a final line shaped like `Session: <id>` or a JSON tail.
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
                        _sessions.Set(jobId, "codex", sessionId);
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
                    _sessions.Set(jobId, "codex", sessionId);
            }
        }

        return trimmed;
    }
}
