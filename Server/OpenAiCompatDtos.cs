using System.Text.Json.Serialization;

namespace Daggeragent.Server;

public sealed class OpenAiChatCompletionRequest
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("messages")] public List<OpenAiRequestMessage>? Messages { get; set; }
    [JsonPropertyName("temperature")] public float? Temperature { get; set; }
    [JsonPropertyName("top_p")] public float? TopP { get; set; }
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
    [JsonPropertyName("max_completion_tokens")] public int? MaxCompletionTokens { get; set; }
    [JsonPropertyName("stream")] public bool? Stream { get; set; }
    // Caller-declared tools are accepted but ignored in v1; DaggerAgent's server-side tools
    // (built-in + MCP) are always offered to the model.
    [JsonPropertyName("tools")] public object? Tools { get; set; }
    [JsonPropertyName("tool_choice")] public object? ToolChoice { get; set; }
}

public sealed class OpenAiRequestMessage
{
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class OpenAiChatCompletion
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("object")] public string Object { get; set; } = "chat.completion";
    [JsonPropertyName("created")] public long Created { get; set; }
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("choices")] public List<OpenAiChoice> Choices { get; set; } = new();
    [JsonPropertyName("usage")] public OpenAiUsage Usage { get; set; } = new();
    [JsonPropertyName("system_fingerprint")] public string? SystemFingerprint { get; set; } = "daggeragent";
}

public sealed class OpenAiChoice
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("message")] public OpenAiResponseMessage Message { get; set; } = new();
    [JsonPropertyName("finish_reason")] public string FinishReason { get; set; } = "stop";
}

public sealed class OpenAiResponseMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "assistant";
    [JsonPropertyName("content")] public string? Content { get; set; }
}

public sealed class OpenAiUsage
{
    [JsonPropertyName("prompt_tokens")] public long PromptTokens { get; set; }
    [JsonPropertyName("completion_tokens")] public long CompletionTokens { get; set; }
    [JsonPropertyName("total_tokens")] public long TotalTokens { get; set; }
}

public sealed class OpenAiChatCompletionChunk
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("object")] public string Object { get; set; } = "chat.completion.chunk";
    [JsonPropertyName("created")] public long Created { get; set; }
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("choices")] public List<OpenAiChunkChoice> Choices { get; set; } = new();
}

public sealed class OpenAiChunkChoice
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("delta")] public OpenAiResponseMessage Delta { get; set; } = new();
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
}

public sealed class OpenAiModelList
{
    [JsonPropertyName("object")] public string Object { get; set; } = "list";
    [JsonPropertyName("data")] public List<OpenAiModel> Data { get; set; } = new();
}

public sealed class OpenAiModel
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("object")] public string Object { get; set; } = "model";
    [JsonPropertyName("created")] public long Created { get; set; }
    [JsonPropertyName("owned_by")] public string OwnedBy { get; set; } = "daggeragent";
}
