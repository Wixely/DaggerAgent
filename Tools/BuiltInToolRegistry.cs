using Microsoft.Extensions.AI;

namespace Daggeragent.Tools;

public sealed class BuiltInToolRegistry
{
    private readonly SpawnSubagentTool _spawn;
    private readonly FilesystemTools _filesystem;
    private readonly ShellToolset _shell;
    private readonly MemoryTools _memory;
    private readonly SystemTools _system;
    private readonly WebTools _web;
    private readonly PlanningTools _planning;

    public BuiltInToolRegistry(
        SpawnSubagentTool spawn,
        FilesystemTools filesystem,
        ShellToolset shell,
        MemoryTools memory,
        SystemTools system,
        WebTools web,
        PlanningTools planning)
    {
        _spawn = spawn;
        _filesystem = filesystem;
        _shell = shell;
        _memory = memory;
        _system = system;
        _web = web;
        _planning = planning;
    }

    public IReadOnlyList<AITool> ForAgent(string? parentJobId, int currentDepth)
    {
        var tools = new List<AITool> { _spawn.Build(parentJobId, currentDepth) };
        tools.AddRange(_filesystem.Build());
        tools.AddRange(_shell.Build());
        tools.AddRange(_memory.Build(parentJobId));
        tools.AddRange(_system.Build());
        tools.AddRange(_web.Build());
        tools.AddRange(_planning.Build(parentJobId));
        return tools;
    }
}
