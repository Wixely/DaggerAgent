using Banter.Agents.Sdk;
using Daggeragent.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Daggeragent.Modes;

/// <summary>
/// DaggerAgent as a Banter room participant: the SDK's <see cref="BanterAgent"/> decides when to
/// speak (delegation, mentions, egress rules), and replies come from DaggerAgent's own
/// <see cref="LlmAgent"/> turn loop — tools, sub-agents and all — rather than a bare
/// chat-completion call.
/// </summary>
public sealed class DaggerBanterAgent : BanterAgent
{
    private readonly LlmAgent _agent;
    private readonly string _model;
    private readonly string? _systemPrompt;
    private readonly ILogger _log;

    /// <summary>
    /// One conversation per room, so the same agent in two rooms holds two conversations and
    /// cannot leak one into the other (mirrors the SDK's LlmChatAgent). Only touched from
    /// RespondAsync, which the base class already serialises through its turn gate.
    /// </summary>
    private readonly Dictionary<string, ConversationState> _states = new(StringComparer.OrdinalIgnoreCase);

    public DaggerBanterAgent(
        BanterAgentOptions options, LlmAgent agent, string model, string? systemPrompt, ILogger log)
        : base(options)
    {
        _agent = agent;
        _model = model;
        _systemPrompt = systemPrompt;
        _log = log;
    }

    protected override async IAsyncEnumerable<string> RespondAsync(
        string room, string sender, string prompt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_states.TryGetValue(room, out var state))
        {
            _states[room] = state = _agent.CreateState(_model, _systemPrompt);
        }

        _log.LogInformation("Banter turn: room={Room} sender={Sender} jobId={JobId}", room, sender, state.Id);

        // Tag the sender: a room has more than one human, and an untagged transcript makes the
        // model lose track of who it is answering.
        var splitter = new ThinkingSplitter();
        await foreach (var update in _agent
            .RunStreamingTurnAsync(state, $"{sender}: {prompt}", cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                // Only plain text reaches the room. The reasoning channel and inline
                // <think> blocks are the model talking to itself, and tool activity is
                // already visible in DaggerAgent's own logs and job stream.
                if (content is not TextContent tc || string.IsNullOrEmpty(tc.Text))
                {
                    continue;
                }

                foreach (var segment in splitter.Push(tc.Text))
                {
                    if (!segment.IsThinking && segment.Text.Length > 0)
                    {
                        yield return segment.Text;
                    }
                }
            }
        }

        if (splitter.Flush() is { IsThinking: false, Text.Length: > 0 } tail)
        {
            yield return tail.Text;
        }

        _log.LogInformation("Banter turn complete: room={Room} jobId={JobId} turns={Turns} tokens={Tokens}",
            room, state.Id, state.TurnsTaken, state.ApproxTokenCount);
    }
}
