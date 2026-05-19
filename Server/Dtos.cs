namespace Daggeragent.Server;

public sealed record CreateJobRequest(string Prompt, string? System, string? Model);

public sealed record CreateJobResponse(string JobId, string Status, string Model, string Text);

public sealed record JobView(
    string JobId,
    string? ParentId,
    string Status,
    string Model,
    string SystemPrompt,
    int TurnsTaken,
    int ApproxTokenCount,
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalThinkingTokens,
    decimal TotalCostUsd,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<MessageView> History);

public sealed record MessageView(string Role, string Text);

public sealed record SendMessageRequest(string Prompt);

public sealed record SendMessageResponse(string JobId, string Status, string Text);
