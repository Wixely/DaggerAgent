using Daggeragent.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daggeragent.Agent;

public sealed class SubAgentManager
{
    private readonly IServiceProvider _services;
    private readonly AgentOptions _agentOptions;
    private readonly OpenAIOptions _openAiOptions;
    private readonly ILogger<SubAgentManager> _log;

    public SubAgentManager(
        IServiceProvider services,
        IOptions<AgentOptions> agentOptions,
        IOptions<OpenAIOptions> openAiOptions,
        ILogger<SubAgentManager> log)
    {
        _services = services;
        _agentOptions = agentOptions.Value;
        _openAiOptions = openAiOptions.Value;
        _log = log;
    }

    public async Task<string> SpawnAsync(string? parentJobId, int depth, string task, string? modelOverride, CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var agent = scope.ServiceProvider.GetRequiredService<LlmAgent>();

        var model = modelOverride ?? _openAiOptions.DefaultModel;
        var systemPrompt = "You are a sub-agent. Complete the assigned task using available tools and return a concise final result.";

        var state = agent.CreateState(model, systemPrompt, parentJobId, depth);

        _log.LogInformation("Spawning sub-agent {JobId} (depth={Depth}, parent={Parent})", state.Id, depth, parentJobId);

        // UseFunctionInvocation drains the tool loop within GetResponseAsync, so a single turn here
        // already represents the sub-agent running to completion (or hitting an internal cap).
        var response = await agent.RunTurnAsync(state, task, cancellationToken).ConfigureAwait(false);
        return response.Text ?? "(sub-agent produced no output)";
    }
}
