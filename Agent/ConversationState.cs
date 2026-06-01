using Microsoft.Extensions.AI;

namespace Daggeragent.Agent;

public sealed class ConversationState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? ParentId { get; set; }
    public string Model { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public int Depth { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public int ApproxTokenCount { get; set; }
    public int TurnsTaken { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public long TotalThinkingTokens { get; set; }
    public decimal TotalCostUsd { get; set; }
    public long LastTurnElapsedMs { get; set; }
    /// <summary>
    /// Wall-clock time of the last turn MINUS the time spent inside tool calls. Used
    /// by the per-turn stats line so the displayed "tokens per second" reflects raw
    /// LLM generation speed rather than getting dragged down by slow tools.
    /// </summary>
    public long LastTurnLlmElapsedMs { get; set; }
    /// <summary>Total tool/MCP function invocations within the last turn.</summary>
    public int LastTurnToolCalls { get; set; }
    /// <summary>
    /// OpenAI-style finish_reason for the last turn — e.g. "stop" (natural end / EOS),
    /// "length" (hit max_tokens), "tool_calls", "content_filter". Null when the server
    /// didn't supply one, which usually means the stream was cut short.
    /// </summary>
    public string? LastTurnFinishReason { get; set; }
    public long LastTurnTotalTokens { get; set; }
    public long LastTurnOutputTokens { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ChatMessage> History { get; set; } = new();

    /// <summary>
    /// Absolute path of the user's working directory at the time the job was created
    /// (captured from <see cref="Configuration.HostLaunchInfo.OriginalWorkingDirectory"/>).
    /// Used by the interactive runner's F8 "Resume last in this dir" feature so a new
    /// session can pick up the most recent job from the same place. Stored alongside
    /// the rest of state in the SQLite blob.
    /// </summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>
    /// Id of the LLM endpoint this job should run against (matches an <see cref="Configuration.EndpointConfig.Id"/>).
    /// Null / empty falls back to <c>EndpointsOptions.DefaultId</c>, then to the first enabled endpoint,
    /// then to the legacy <c>OpenAIOptions</c>. Lets each job stick with whichever provider it was
    /// started against even if the global default changes mid-session.
    /// </summary>
    public string? EndpointId { get; set; }
}
