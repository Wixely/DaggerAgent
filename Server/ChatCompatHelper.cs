using Daggeragent.Configuration;
using Daggeragent.Llm;
using Daggeragent.Mcp;
using Daggeragent.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Daggeragent.Server;

internal static class ChatCompatHelper
{
    public sealed record Result(ChatResponse? Response, string? Error);

    public static string ResolveModel(string? requested, OpenAIOptions openAi) =>
        !string.IsNullOrEmpty(requested) ? requested! : openAi.DefaultModel;

    public static (IChatClient Client, ChatOptions Options) BuildClientAndOptions(
        string model,
        float? temperature,
        float? topP,
        int? maxOutputTokens,
        ChatClientFactory chatFactory,
        BuiltInToolRegistry builtIns,
        McpToolProvider mcpTools)
    {
        var tools = new List<AITool>();
        tools.AddRange(builtIns.ForAgent(parentJobId: null, currentDepth: 0));
        tools.AddRange(mcpTools.GetTools());

        var options = new ChatOptions
        {
            ModelId = model,
            Tools = tools.Count > 0 ? tools : null,
            Temperature = temperature,
            TopP = topP,
            MaxOutputTokens = maxOutputTokens,
        };
        return (chatFactory.Create(model), options);
    }

    public static async Task<Result> RunOneShotAsync(
        IList<ChatMessage> messages,
        string model,
        float? temperature,
        float? topP,
        int? maxOutputTokens,
        ChatClientFactory chatFactory,
        BuiltInToolRegistry builtIns,
        McpToolProvider mcpTools,
        CancellationToken ct)
    {
        var (client, options) = BuildClientAndOptions(model, temperature, topP, maxOutputTokens, chatFactory, builtIns, mcpTools);
        try
        {
            var response = await client.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
            return new Result(response, null);
        }
        catch (Exception ex)
        {
            return new Result(null, ex.Message);
        }
    }
}
