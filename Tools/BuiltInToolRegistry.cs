using Daggeragent.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

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
    private readonly ToolResultTools _toolResults;
    private readonly CliDelegationTools _cliDelegation;
    private readonly AgentOptions _agentOptions;
    private readonly ToolsOptions _toolsOptions;

    public BuiltInToolRegistry(
        SpawnSubagentTool spawn,
        FilesystemTools filesystem,
        ShellToolset shell,
        MemoryTools memory,
        SystemTools system,
        WebTools web,
        PlanningTools planning,
        ToolResultTools toolResults,
        CliDelegationTools cliDelegation,
        IOptions<AgentOptions> agentOptions,
        IOptions<ToolsOptions> toolsOptions)
    {
        _spawn = spawn;
        _filesystem = filesystem;
        _shell = shell;
        _memory = memory;
        _system = system;
        _web = web;
        _planning = planning;
        _toolResults = toolResults;
        _cliDelegation = cliDelegation;
        _agentOptions = agentOptions.Value;
        _toolsOptions = toolsOptions.Value;
    }

    public IReadOnlyList<AITool> ForAgent(string? parentJobId, int currentDepth, string? parentEndpointId = null, string? parentModel = null)
    {
        var tools = new List<AITool>();
        // Don't even register spawn_subagent on a sub-agent that's at the recursion limit —
        // saves a refusing tool round-trip and stops the model trying to delegate forever.
        if (currentDepth < _agentOptions.MaxSubAgentDepth)
            tools.Add(_spawn.Build(parentJobId, currentDepth, parentEndpointId, parentModel));
        tools.AddRange(_filesystem.Build());
        tools.AddRange(_shell.Build());
        tools.AddRange(_memory.Build(parentJobId));
        tools.AddRange(_system.Build());
        tools.AddRange(_web.Build());
        tools.AddRange(_planning.Build(parentJobId));
        // Tool-result consumer tools are only useful once an offload has happened.
        // Skip registering them when the offload feature is disabled.
        if (_toolsOptions.MaxToolResultChars > 0)
            tools.AddRange(_toolResults.Build(parentJobId));
        // delegate_to_claude / delegate_to_codex — gated inside .Build() by AllowCliDelegation.
        tools.AddRange(_cliDelegation.Build(parentJobId));
        return tools;
    }
}
