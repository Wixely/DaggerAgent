namespace Daggeragent.Configuration;

public sealed class OpenAIOptions
{
    public const string SectionName = "OpenAI";

    /// <summary>
    /// Wire protocol used to reach the upstream LLM.
    /// <list type="bullet">
    ///   <item><c>OpenAI</c> — OpenAI Chat Completions REST shape (OpenAI, Azure OpenAI,
    ///   LM Studio, vLLM, any OpenAI-compatible endpoint).</item>
    ///   <item><c>Ollama</c> — Ollama's native /api/chat protocol via OllamaSharp.</item>
    ///   <item><c>Anthropic</c> — Anthropic's Claude API via its OpenAI-compatible endpoint
    ///   (<c>https://api.anthropic.com/v1/</c>). Set <see cref="ApiKey"/> to your
    ///   <c>sk-ant-…</c> key and <see cref="DefaultModel"/> to e.g. <c>claude-opus-4-7</c>.
    ///   Leave <see cref="BaseUrl"/> blank to use Anthropic's URL; set it to override.</item>
    /// </list>
    /// </summary>
    public string Provider { get; set; } = "OpenAI";

    public string BaseUrl { get; set; } = "http://localhost:1234/v1";
    public string ApiKey { get; set; } = "";
    public string DefaultModel { get; set; } = "qwen/qwen3.6-27b";
    public int RequestTimeoutSeconds { get; set; } = 120;
}
