using System.ClientModel;
using Daggeragent.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OpenAI;

namespace Daggeragent.Llm;

public sealed class EmbeddingClientFactory
{
    private readonly OpenAIOptions _llmOptions;
    private readonly MemoryOptions _memoryOptions;

    public EmbeddingClientFactory(IOptions<OpenAIOptions> llmOptions, IOptions<MemoryOptions> memoryOptions)
    {
        _llmOptions = llmOptions.Value;
        _memoryOptions = memoryOptions.Value;
    }

    public IEmbeddingGenerator<string, Embedding<float>> Create()
    {
        var model = _memoryOptions.EmbeddingModel;
        return _llmOptions.Provider?.Trim().ToLowerInvariant() switch
        {
            "ollama" => new OllamaApiClient(new Uri(_llmOptions.BaseUrl), model),
            _ => CreateOpenAi(model),
        };
    }

    private IEmbeddingGenerator<string, Embedding<float>> CreateOpenAi(string model)
    {
        var apiKey = string.IsNullOrWhiteSpace(_llmOptions.ApiKey) ? "no-key-required" : _llmOptions.ApiKey;
        var client = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(_llmOptions.BaseUrl) });
        return client.GetEmbeddingClient(model).AsIEmbeddingGenerator();
    }
}
