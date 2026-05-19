using System.ClientModel;
using Daggeragent.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OpenAI;

namespace Daggeragent.Llm;

public sealed class ChatClientFactory
{
    private readonly OpenAIOptions _options;
    private readonly AgentOptions _agentOptions;
    private readonly ILoggerFactory _loggerFactory;

    public ChatClientFactory(IOptions<OpenAIOptions> options, IOptions<AgentOptions> agentOptions, ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _agentOptions = agentOptions.Value;
        _loggerFactory = loggerFactory;
    }

    public IChatClient Create(string? modelOverride = null)
    {
        var model = modelOverride ?? _options.DefaultModel;
        IChatClient baseClient = _options.Provider?.Trim().ToLowerInvariant() switch
        {
            "ollama" => new OllamaApiClient(new Uri(_options.BaseUrl), model),
            _ => CreateOpenAiClient(model),
        };

        return new ChatClientBuilder(baseClient)
            .UseFunctionInvocation(_loggerFactory, fic =>
            {
                // Without this, MEAI's default cap (10 iterations) silently overrides the
                // configured Agent:MaxTurnsPerInvocation. After the cap the middleware
                // returns whatever the model emitted last — which often is "let me call
                // another tool" forever for poorly-behaved models.
                fic.MaximumIterationsPerRequest = Math.Max(1, _agentOptions.MaxTurnsPerInvocation);
            })
            .UseLogging(_loggerFactory)
            .Build();
    }

    private IChatClient CreateOpenAiClient(string model)
    {
        var apiKey = string.IsNullOrWhiteSpace(_options.ApiKey) ? "no-key-required" : _options.ApiKey;
        // NetworkTimeout defaults to 100s, which a slow local LLM blows through during
        // long thinking phases — the request then gets cancelled and our turn dies. Bind
        // it to OpenAIOptions.RequestTimeoutSeconds (default 120s, configurable up).
        var openAi = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(_options.BaseUrl),
                NetworkTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds)),
            });
        return openAi.GetChatClient(model).AsIChatClient();
    }
}
