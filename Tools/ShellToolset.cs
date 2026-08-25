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

            // No cancellation token on the reads: killing the process closes the pipes, which
            // ends these naturally with everything the command wrote. See ProcessOutput for why
            // passing the token instead loses the output it is meant to preserve.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                var partialOut = await ProcessOutput.ReadPartialAsync(stdoutTask).ConfigureAwait(false);
                var partialErr = await ProcessOutput.ReadPartialAsync(stderrTask).ConfigureAwait(false);

                var timedOut = new StringBuilder();
                timedOut.Append("Error: command timed out after ").Append(_options.ShellTimeoutSeconds)
                        .AppendLine("s and was killed; partial output follows.");
                timedOut.Append("interpreter: ").AppendLine(file);
                AppendStreams(timedOut, partialOut, partialErr);
                return timedOut.ToString();
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            var sb = new StringBuilder();
            sb.Append("interpreter: ").AppendLine(file);
            sb.Append("exit_code: ").AppendLine(proc.ExitCode.ToString());
            AppendStreams(sb, stdout, stderr);
            return sb.ToString();
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    private static void AppendStreams(StringBuilder sb, string stdout, string stderr)
    {
        if (!string.IsNullOrEmpty(stdout)) sb.AppendLine("---stdout---").Append(stdout);
        if (!string.IsNullOrEmpty(stderr)) sb.AppendLine("---stderr---").Append(stderr);
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
