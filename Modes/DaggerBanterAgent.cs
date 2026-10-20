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
    private readonly string? _endpointId;
    private readonly string? _systemPrompt;
    private readonly ILogger _log;

    /// <summary>
    /// One conversation per room, so the same agent in two rooms holds two conversations and
    /// cannot leak one into the other (mirrors the SDK's LlmChatAgent). Only touched from
    /// RespondAsync, which the base class already serialises through its turn gate.
    /// </summary>
    private readonly Dictionary<string, ConversationState> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each room's system prompt as built, before any persona — so a persona that
    /// changes or is cleared replaces the old one instead of stacking on it.</summary>
    private readonly Dictionary<string, string> _basePrompts = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="model">Model for room turns; empty lets the endpoint's default decide.</param>
    /// <param name="endpointId">LLM endpoint id; null/empty uses the active default endpoint.</param>
    public DaggerBanterAgent(
        BanterAgentOptions options, LlmAgent agent, string model, string? endpointId,
        string? systemPrompt, ILogger log)
        : base(options)
    {
        _agent = agent;
        _model = model;
        _endpointId = string.IsNullOrWhiteSpace(endpointId) ? null : endpointId;
        _systemPrompt = systemPrompt;
        _log = log;
    }

    // ── Connection lifecycle, surfaced for the host ──────────────────────────────────────────
    // The SDK's client redials, re-auths and rejoins on its own (and the base class re-announces);
    // these forwarders exist so BanterConnectionService can show that honestly instead of a
    // status card that says "connected" through an outage.

    /// <summary>The connection dropped; the client is about to start redialling.</summary>
    public event Action? ConnectionLost;

    /// <summary>A redial attempt is starting (1-based).</summary>
    public event Action<int>? ConnectionRetrying;

    /// <summary>Back: re-authenticated, rooms rejoined, attributes re-announced.</summary>
    public event Action? ConnectionRestored;

    /// <summary>
    /// The server ended the session on purpose — identity revoked, re-keyed or removed — and the
    /// client will not redial, because the credential it would redial with is what just stopped
    /// existing. Terminal until a human re-enrols.
    /// </summary>
    public event Action<string>? Evicted;

    /// <summary>
    /// Call once after <see cref="BanterAgent.StartAsync"/>: the client only exists from then.
    /// </summary>
    public void WireConnectionEvents()
    {
        Client.Disconnected += () => ConnectionLost?.Invoke();
        Client.Reconnecting += attempt => ConnectionRetrying?.Invoke(attempt);
        Client.Reconnected += () => ConnectionRestored?.Invoke();
        Client.Evicted += reason => Evicted?.Invoke(reason);
    }

    protected override async IAsyncEnumerable<string> RespondAsync(
        string room, string sender, string prompt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_states.TryGetValue(room, out var state))
        {
            _states[room] = state = _agent.CreateState(_model, _systemPrompt);
            state.EndpointId = _endpointId;
            _basePrompts[room] = state.SystemPrompt;
        }
        ApplyPersona(room, state);

        // The room's own tools ride along with Dagger's registry rather than being replaced by
        // it (issue #10): ask_operator for structured questions, the delegator's open_side_room /
        // invite_agent, and whatever the server granted. Rebuilt per turn because the list is
        // situational — delegator status changes with elections, grants change under admins —
        // and dispatched back through the SDK so local-vs-server routing stays its problem.
        var roomTools = ToolsFor(room)
            .Select(AITool (d) => new BanterRoomToolFunction(d, room, (name, args, r, ct) => InvokeToolAsync(name, args, r, ct)))
            .ToList();

        _log.LogInformation("Banter turn: room={Room} sender={Sender} jobId={JobId} roomTools=[{RoomTools}]",
            room, sender, state.Id, string.Join(", ", ToolsFor(room).Select(t => t.Name)));

        // Tag the sender: a room has more than one human, and an untagged transcript makes the
        // model lose track of who it is answering.
        var splitter = new ThinkingSplitter();
        await foreach (var update in _agent
            .RunStreamingTurnAsync(state, $"{sender}: {prompt}", attachments: null, roomTools, cancellationToken)
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

    /// <summary>
    /// Put the persona in force — an admin's from the agents page, else Banter:Persona — after
    /// the room's system prompt, as the SDK's own LlmChatAgent does. Read every turn, so an
    /// admin's change reaches the next answer rather than the next connect. The system prompt is
    /// the first history message, and compression keeps it there.
    /// </summary>
    private void ApplyPersona(string room, ConversationState state)
    {
        var basePrompt = _basePrompts[room];
        var persona = Persona;
        var prompt = persona.Length > 0 ? $"{basePrompt}\n\n{persona}" : basePrompt;
        if (prompt == state.SystemPrompt) return;

        state.SystemPrompt = prompt;
        if (state.History.Count > 0 && state.History[0].Role == ChatRole.System)
            state.History[0] = new ChatMessage(ChatRole.System, prompt);
        _log.LogInformation("Banter persona {Change} for room={Room}",
            persona.Length > 0 ? "applied" : "cleared", room);
    }
}
