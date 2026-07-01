namespace Daggeragent.Server;

public sealed record CreateJobRequest(string Prompt, string? System, string? Model, string? EndpointId = null);

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
    IReadOnlyList<MessageView> History,
    string? WorkingDirectory = null,
    string? EndpointId = null);

public sealed record MessageView(string Role, string Text);

public sealed record SendMessageRequest(string Prompt);

public sealed record SendMessageResponse(string JobId, string Status, string Text);

// ─────────────────────────── Web UI DTOs ───────────────────────────

/// <summary>
/// Streaming-create variant of CreateJobRequest. Adds optional working directory + image
/// attachments (base64-encoded) so the UI can submit multimodal prompts to the agent.
/// </summary>
public sealed record CreateJobStreamRequest(
    string Prompt,
    string? System,
    string? Model,
    string? WorkingDirectory,
    IReadOnlyList<ImageInput>? Images,
    string? EndpointId = null);

/// <summary>Streaming-continuation variant of SendMessageRequest. Allows model + images + per-turn working dir + endpoint.</summary>
public sealed record SendMessageStreamRequest(
    string Prompt,
    string? Model,
    string? WorkingDirectory,
    IReadOnlyList<ImageInput>? Images,
    string? EndpointId = null);

/// <summary>One image attachment from the UI. Base64 is just the raw encoding — no data: prefix.</summary>
public sealed record ImageInput(string MediaType, string Base64);

/// <summary>
/// Patch shape for ToolsOptions — every field optional so the UI can flip one toggle
/// without having to re-send the whole settings object.
/// </summary>
public sealed record ToolsOptionsPatch(
    string? WorkingDirectory = null,
    bool? AllowAnyPath = null,
    bool? ReadOnly = null,
    bool? AllowWrite = null,
    bool? WritePreview = null,
    bool? AllowShell = null,
    int? MaxFileBytes = null,
    int? MaxResults = null,
    int? ShellTimeoutSeconds = null,
    bool? GranularTools = null,
    bool? ForcePlan = null,
    int? ReadFileSummaryThresholdBytes = null,
    int? MaxToolResultChars = null,
    bool? AllowCliDelegation = null,
    string? ClaudeCliPath = null,
    string? CodexCliPath = null,
    string? CopilotCliPath = null);

/// <summary>Snapshot of the current ToolsOptions — what GET /agent/settings returns.</summary>
public sealed record ToolsSettingsView(
    string WorkingDirectory,
    bool AllowAnyPath,
    bool ReadOnly,
    bool AllowWrite,
    bool WritePreview,
    bool AllowShell,
    int MaxFileBytes,
    int MaxResults,
    int ShellTimeoutSeconds,
    bool GranularTools,
    bool ForcePlan,
    int ReadFileSummaryThresholdBytes,
    int MaxToolResultChars,
    bool AllowCliDelegation,
    string ClaudeCliPath,
    string CodexCliPath,
    string CopilotCliPath);

/// <summary>Each row of GET /agent/pending-writes — a staged change with a rendered unified diff.</summary>
public sealed record PendingWriteView(
    string AbsolutePath,
    string DisplayPath,
    int OldLength,
    int NewLength,
    DateTimeOffset StagedAt,
    string UnifiedDiff);

/// <summary>One built-in or MCP tool, surfaced to the UI's Tools tab.</summary>
public sealed record ToolListItem(
    string Name,
    string? Description,
    string Category,
    string Origin,
    bool Enabled,
    string? DisabledReason);

/// <summary>MCP server entry surfaced on the MCP tab.</summary>
public sealed record McpServerView(
    string Name,
    string Status,
    string Transport,
    int ToolCount,
    string? Detail,
    IReadOnlyList<McpToolView> Tools);

public sealed record McpToolView(string Name, string? Description);

/// <summary>Slash-command catalogue entry returned by GET /agent/commands.</summary>
public sealed record CommandView(string Command, string Description, string? UsageHint);

/// <summary>Current plan for a job — fed to the Plan tab.</summary>
public sealed record PlanView(
    string JobId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<PlanStepView> Steps);

public sealed record PlanStepView(string Description, string Status, string? Note);
