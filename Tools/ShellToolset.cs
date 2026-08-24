using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Daggeragent.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Daggeragent.Tools;

public sealed class ShellToolset
{
    private readonly ToolsOptions _options;
    private readonly HostLaunchInfo _launchInfo;

    public ShellToolset(IOptions<ToolsOptions> options, HostLaunchInfo launchInfo)
    {
        _options = options.Value;
        _launchInfo = launchInfo;
    }

    public IEnumerable<AITool> Build()
    {
        if (_options.ReadOnly || !_options.AllowShell) yield break;
        yield return AIFunctionFactory.Create(ExecShell, name: "exec_shell", description:
            "Execute a shell command under the configured working directory. The `shell` parameter " +
            "picks the interpreter: 'auto' (default — PowerShell on Windows, bash elsewhere), " +
            "'cmd', 'powershell', 'pwsh' (cross-platform), 'bash', or 'sh'. Returns stdout, stderr, " +
            "and exit code. Timeout: " + _options.ShellTimeoutSeconds + "s.");
    }

    [Description("Run a shell command and return its output.")]
    private async Task<string> ExecShell(
        [Description("The full command line to execute. The chosen shell parses it.")] string command,
        [Description("Interpreter: auto | cmd | powershell | pwsh | bash | sh.")] string shell = "auto",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (file, args) = ResolveShell(shell, command);
            if (file is null) return $"Error: shell '{shell}' is not available on this host.";

            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = WorkingDirectory(),
            };

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start shell process.");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.ShellTimeoutSeconds));

            // Pump into buffers rather than ReadToEndAsync: on timeout the latter throws and
            // discards everything it had read, so a command that ran the full timeout returned
            // nothing at all. Buffering keeps the partial output available to the timeout path.
            var stdoutBuf = new StringBuilder();
            var stderrBuf = new StringBuilder();
            var stdoutTask = PumpAsync(proc.StandardOutput, stdoutBuf, cts.Token);
            var stderrTask = PumpAsync(proc.StandardError, stderrBuf, cts.Token);

            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                // Drain the reader tasks so they don't surface as unobserved exceptions once
                // Kill closes the pipes, and so the buffers are settled before we read them.
                try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); } catch { }

                var timedOut = new StringBuilder();
                timedOut.Append("Error: command timed out after ").Append(_options.ShellTimeoutSeconds)
                        .AppendLine("s and was killed; partial output follows.");
                timedOut.Append("interpreter: ").AppendLine(file);
                AppendStreams(timedOut, stdoutBuf, stderrBuf);
                return timedOut.ToString();
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

            var sb = new StringBuilder();
            sb.Append("interpreter: ").AppendLine(file);
            sb.Append("exit_code: ").AppendLine(proc.ExitCode.ToString());
            AppendStreams(sb, stdoutBuf, stderrBuf);
            return sb.ToString();
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // Reads incrementally so whatever arrived before cancellation survives in `sink`.
    // Cancellation and a pipe closed by Kill are both normal ends of stream here, not errors.
    private static async Task PumpAsync(StreamReader reader, StringBuilder sink, CancellationToken ct)
    {
        var buffer = new char[4096];
        while (true)
        {
            int read;
            try { read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; }
            catch (ObjectDisposedException) { break; }
            if (read == 0) break;
            sink.Append(buffer, 0, read);
        }
    }

    private static void AppendStreams(StringBuilder sb, StringBuilder stdout, StringBuilder stderr)
    {
        if (stdout.Length > 0) sb.AppendLine("---stdout---").Append(stdout);
        if (stderr.Length > 0) sb.AppendLine("---stderr---").Append(stderr);
    }

    private static (string? File, string Args) ResolveShell(string shell, string command)
    {
        var s = (shell ?? "auto").Trim().ToLowerInvariant();
        var isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        if (s == "auto")
        {
            if (isWin)
            {
                var pwsh = FindOnPath("pwsh") ?? FindOnPath("pwsh.exe");
                if (pwsh is not null) return (pwsh, $"-NoProfile -Command \"{EscapeForPwsh(command)}\"");
                var powershell = FindOnPath("powershell.exe");
                if (powershell is not null) return (powershell, $"-NoProfile -Command \"{EscapeForPwsh(command)}\"");
                return (FindOnPath("cmd.exe") ?? "cmd.exe", $"/c {command}");
            }
            return (FindOnPath("bash") ?? "/bin/bash", $"-c \"{EscapeForBash(command)}\"");
        }

        return s switch
        {
            "cmd"        => (FindOnPath("cmd.exe"),        $"/c {command}"),
            "powershell" => (FindOnPath("powershell.exe"), $"-NoProfile -Command \"{EscapeForPwsh(command)}\""),
            "pwsh"       => (FindOnPath("pwsh") ?? FindOnPath("pwsh.exe"), $"-NoProfile -Command \"{EscapeForPwsh(command)}\""),
            "bash"       => (FindOnPath("bash") ?? FindOnPath("bash.exe"), $"-c \"{EscapeForBash(command)}\""),
            "sh"         => (FindOnPath("sh") ?? FindOnPath("sh.exe"),     $"-c \"{EscapeForBash(command)}\""),
            _            => (null, ""),
        };
    }

    private static string EscapeForBash(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string EscapeForPwsh(string s) => s.Replace("`", "``").Replace("\"", "`\"");

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    private string WorkingDirectory()
    {
        var configured = ToolExecutionContext.WorkingDirectory ?? _options.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(configured)) return _launchInfo.OriginalWorkingDirectory;
        return Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(_launchInfo.OriginalWorkingDirectory, configured));
    }
}
