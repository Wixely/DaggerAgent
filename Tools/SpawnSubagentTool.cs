using System.ComponentModel;
using Daggeragent.Agent;
using Daggeragent.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Daggeragent.Tools;

public sealed class SpawnSubagentTool
{
    private readonly SubAgentManager _subAgentManager;
    private readonly AgentOptions _agentOptions;

    public SpawnSubagentTool(SubAgentManager subAgentManager, IOptions<AgentOptions> agentOptions)
    {
        _subAgentManager = subAgentManager;
        _agentOptions = agentOptions.Value;
    }

    public AITool Build(string? parentJobId, int currentDepth, string? parentEndpointId = null, string? parentModel = null)
    {
        var maxDepth = _agentOptions.MaxSubAgentDepth;

        async Task<string> SpawnSubagent(
            [Description("The task or question for the sub-agent to perform.")] string task,
            [Description("Optional model name override. Falls back to the configured default if omitted.")] string? model = null,
            CancellationToken cancellationToken = default)
        {
            if (currentDepth >= maxDepth)
            {
                return $"Error: max sub-agent depth ({maxDepth}) reached.";
            }

            var result = await _subAgentManager.SpawnAsync(
                parentJobId: parentJobId,
                depth: currentDepth + 1,
                task: task,
                modelOverride: model,
                parentEndpointId: parentEndpointId,
                parentModel: parentModel,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return result;
        }

        return AIFunctionFactory.Create(
            SpawnSubagent,
            name: "spawn_subagent",
            description: "Spawn a sub-agent with isolated context to perform a delegated task. The sub-agent has its own conversation history and tool budget. Returns the sub-agent's final assistant message.");
    }
}
