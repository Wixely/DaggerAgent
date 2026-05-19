using System.Text.Json.Serialization;

namespace Daggeragent.Server;

public sealed class OllamaChatRequest
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("messages")] public List<OllamaMessage>? Messages { get; set; }
    [JsonPropertyName("stream")] public bool? Stream { get; set; }
    [JsonPropertyName("format")] public object? Format { get; set; }
    [JsonPropertyName("options")] public OllamaOptions? Options { get; set; }
    [JsonPropertyName("keep_alive")] public object? KeepAlive { get; set; }
}

public sealed class OllamaGenerateRequest
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("prompt")] public string? Prompt { get; set; }
    [JsonPropertyName("system")] public string? System { get; set; }
    [JsonPropertyName("template")] public string? Template { get; set; }
    [JsonPropertyName("stream")] public bool? Stream { get; set; }
    [JsonPropertyName("raw")] public bool? Raw { get; set; }
    [JsonPropertyName("format")] public object? Format { get; set; }
    [JsonPropertyName("options")] public OllamaOptions? Options { get; set; }
    [JsonPropertyName("keep_alive")] public object? KeepAlive { get; set; }
}

public sealed class OllamaShowRequest
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
}

public sealed class OllamaOptions
{
    [JsonPropertyName("temperature")] public float? Temperature { get; set; }
    [JsonPropertyName("top_p")] public float? TopP { get; set; }
    [JsonPropertyName("num_predict")] public int? NumPredict { get; set; }
    [JsonPropertyName("num_ctx")] public int? NumCtx { get; set; }
}

public sealed class OllamaMessage
{
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
}

public sealed class OllamaChatResponse
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("message")] public OllamaMessage Message { get; set; } = new();
    [JsonPropertyName("done")] public bool Done { get; set; } = true;
    [JsonPropertyName("done_reason")] public string DoneReason { get; set; } = "stop";
    [JsonPropertyName("total_duration")] public long TotalDuration { get; set; }
    [JsonPropertyName("load_duration")] public long LoadDuration { get; set; }
    [JsonPropertyName("prompt_eval_count")] public long PromptEvalCount { get; set; }
    [JsonPropertyName("prompt_eval_duration")] public long PromptEvalDuration { get; set; }
    [JsonPropertyName("eval_count")] public long EvalCount { get; set; }
    [JsonPropertyName("eval_duration")] public long EvalDuration { get; set; }
}

public sealed class OllamaGenerateResponse
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("response")] public string Response { get; set; } = "";
    [JsonPropertyName("done")] public bool Done { get; set; } = true;
    [JsonPropertyName("done_reason")] public string DoneReason { get; set; } = "stop";
    [JsonPropertyName("total_duration")] public long TotalDuration { get; set; }
    [JsonPropertyName("load_duration")] public long LoadDuration { get; set; }
    [JsonPropertyName("prompt_eval_count")] public long PromptEvalCount { get; set; }
    [JsonPropertyName("prompt_eval_duration")] public long PromptEvalDuration { get; set; }
    [JsonPropertyName("eval_count")] public long EvalCount { get; set; }
    [JsonPropertyName("eval_duration")] public long EvalDuration { get; set; }
    [JsonPropertyName("context")] public int[] Context { get; set; } = Array.Empty<int>();
}

public sealed class OllamaTagsResponse
{
    [JsonPropertyName("models")] public List<OllamaTagsModel> Models { get; set; } = new();
}

public sealed class OllamaTagsModel
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("modified_at")] public string ModifiedAt { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("digest")] public string Digest { get; set; } = "";
    [JsonPropertyName("details")] public OllamaModelDetails Details { get; set; } = new();
}

public sealed class OllamaModelDetails
{
    [JsonPropertyName("parent_model")] public string ParentModel { get; set; } = "";
    [JsonPropertyName("format")] public string Format { get; set; } = "openai";
    [JsonPropertyName("family")] public string Family { get; set; } = "daggeragent";
    [JsonPropertyName("families")] public string[] Families { get; set; } = new[] { "daggeragent" };
    [JsonPropertyName("parameter_size")] public string ParameterSize { get; set; } = "unknown";
    [JsonPropertyName("quantization_level")] public string QuantizationLevel { get; set; } = "unknown";
}

public sealed class OllamaShowResponse
{
    [JsonPropertyName("modelfile")] public string Modelfile { get; set; } = "";
    [JsonPropertyName("parameters")] public string Parameters { get; set; } = "";
    [JsonPropertyName("template")] public string Template { get; set; } = "";
    [JsonPropertyName("details")] public OllamaModelDetails Details { get; set; } = new();
}

public sealed class OllamaVersionResponse
{
    [JsonPropertyName("version")] public string Version { get; set; } = "0.1.0-daggeragent";
}
