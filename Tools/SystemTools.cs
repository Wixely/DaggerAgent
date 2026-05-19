using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Daggeragent.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Daggeragent.Tools;

public sealed class SystemTools
{
    private readonly ToolsOptions _options;
    private readonly HostLaunchInfo _launchInfo;

    public SystemTools(IOptions<ToolsOptions> options, HostLaunchInfo launchInfo)
    {
        _options = options.Value;
        _launchInfo = launchInfo;
    }

    public IEnumerable<AITool> Build()
    {
        yield return AIFunctionFactory.Create(Pwd, name: "pwd", description:
            "Return the agent's working directory (the root that all filesystem tools operate within).");
        yield return AIFunctionFactory.Create(Which, name: "which", description:
            "Locate an executable on PATH. Returns the full path or an error if not found.");
        yield return AIFunctionFactory.Create(ListProcesses, name: "list_processes", description:
            "List running processes (top N by memory). Read-only.");
    }

    [Description("Print current working directory.")]
    private string Pwd()
    {
        var configured = _options.WorkingDirectory;
        var root = string.IsNullOrWhiteSpace(configured)
            ? _launchInfo.OriginalWorkingDirectory
            : Path.IsPathRooted(configured)
                ? Path.GetFullPath(configured)
                : Path.GetFullPath(Path.Combine(_launchInfo.OriginalWorkingDirectory, configured));
        return root;
    }

    [Description("Find an executable on PATH.")]
    private string Which(
        [Description("Executable name. On Windows the .exe suffix is added automatically if needed.")] string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return "Error: PATH not set.";

        var candidates = new List<string> { name };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !name.Contains('.'))
        {
            candidates.AddRange(new[] { name + ".exe", name + ".cmd", name + ".bat" });
        }

        foreach (var dir in path.Split(Path.PathSeparator))
        {
            foreach (var c in candidates)
            {
                try
                {
                    var full = Path.Combine(dir, c);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
        }
        return $"Error: '{name}' not found on PATH.";
    }

    [Description("List running processes.")]
    private string ListProcesses(
        [Description("Maximum number of processes to return (sorted by working-set memory). Default 20.")] int limit = 20)
    {
        try
        {
            var processes = Process.GetProcesses()
                .OrderByDescending(p =>
                {
                    try { return p.WorkingSet64; }
                    catch { return 0L; }
                })
                .Take(limit);

            var sb = new StringBuilder();
            sb.AppendLine("pid     mem_mb   name");
            foreach (var p in processes)
            {
                try
                {
                    sb.Append(p.Id.ToString().PadLeft(6)).Append(' ');
                    sb.Append((p.WorkingSet64 / 1024 / 1024).ToString().PadLeft(7)).Append("  ");
                    sb.AppendLine(p.ProcessName);
                }
                catch { /* process may have exited between enumeration and read */ }
            }
            return sb.ToString();
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }
}
