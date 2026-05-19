namespace Daggeragent.Configuration;

public sealed class OpenAIOptions
{
    public const string SectionName = "OpenAI";

    /// <summary>
    /// Wire protocol used to reach the upstream LLM. "OpenAI" speaks the OpenAI Chat
    /// Completions REST shape (works for OpenAI, Azure OpenAI, LM Studio, vLLM, any
    /// OpenAI-compatible endpoint). "Ollama" speaks Ollama's native /api/chat protocol
    /// via OllamaSharp.
    /// </summary>
    public string Provider { get; set; } = "OpenAI";

    public string BaseUrl { get; set; } = "http://localhost:1234/v1";
    public string ApiKey { get; set; } = "";
    public string DefaultModel { get; set; } = "qwen/qwen3.6-27b";
    public int RequestTimeoutSeconds { get; set; } = 120;
}
