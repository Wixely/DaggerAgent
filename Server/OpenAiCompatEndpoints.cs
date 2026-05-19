using Daggeragent.Configuration;
using Daggeragent.Llm;
using Daggeragent.Mcp;
using Daggeragent.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Daggeragent.Server;

public static class OpenAiCompatEndpoints
{
    public static IEndpointRouteBuilder MapOpenAiCompatApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/models", (IOptions<OpenAIOptions> openAiOpts) =>
        {
            var model = openAiOpts.Value.DefaultModel;
            return Results.Ok(new OpenAiModelList
            {
                Data = new List<OpenAiModel>
                {
                    new()
                    {
                        Id = model,
                        Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    },
                },
            });
        });

        app.MapPost("/v1/chat/completions", async (
            HttpContext httpContext,
            OpenAiChatCompletionRequest req,
            ChatClientFactory chatFactory,
            BuiltInToolRegistry builtIns,
            McpToolProvider mcpTools,
            IOptions<AgentOptions> agentOpts,
            IOptions<OpenAIOptions> openAiOpts,
            CancellationToken ct) =>
        {
            var model = ChatCompatHelper.ResolveModel(req.Model, openAiOpts.Value);

            var messages = (req.Messages ?? new())
                .Where(m => !string.IsNullOrEmpty(m.Content))
                .Select(m => new ChatMessage(MapRole(m.Role), m.Content ?? "") { AuthorName = m.Name })
                .ToList();

            if (messages.Count == 0)
            {
                return Results.BadRequest(new { error = new { message = "messages array is empty.", type = "invalid_request_error" } });
            }

            if (req.Stream == true)
            {
                await StreamCompletionAsync(httpContext, messages, model, req, chatFactory, builtIns, mcpTools, ct).ConfigureAwait(false);
                return Results.Empty;
            }

            var result = await ChatCompatHelper.RunOneShotAsync(
                messages, model, req.Temperature, req.TopP,
                req.MaxCompletionTokens ?? req.MaxTokens,
                chatFactory, builtIns, mcpTools, ct).ConfigureAwait(false);

            if (result.Response is null)
            {
                return Results.Json(
                    new { error = new { message = result.Error, type = "upstream_error" } },
                    statusCode: StatusCodes.Status502BadGateway);
            }
            var response = result.Response;

            var completion = new OpenAiChatCompletion
            {
                Id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..24],
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model = model,
                Choices = new List<OpenAiChoice>
                {
                    new()
                    {
                        Index = 0,
                        Message = new OpenAiResponseMessage { Role = "assistant", Content = response.Text ?? "" },
                        FinishReason = MapFinishReason(response.FinishReason),
                    },
                },
                Usage = new OpenAiUsage
                {
                    PromptTokens = response.Usage?.InputTokenCount ?? 0,
                    CompletionTokens = response.Usage?.OutputTokenCount ?? 0,
                    TotalTokens = response.Usage?.TotalTokenCount ?? 0,
                },
            };
            return Results.Ok(completion);
        });

        return app;
    }

    private static async Task StreamCompletionAsync(
        HttpContext http,
        IList<ChatMessage> messages,
        string model,
        OpenAiChatCompletionRequest req,
        ChatClientFactory chatFactory,
        BuiltInToolRegistry builtIns,
        McpToolProvider mcpTools,
        CancellationToken ct)
    {
        var id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..24];
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        http.Response.Headers["Content-Type"] = "text/event-stream";
        http.Response.Headers["Cache-Control"] = "no-cache";
        http.Response.Headers["Connection"] = "keep-alive";

        var serializerOptions = new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

        var (client, options) = ChatCompatHelper.BuildClientAndOptions(
            model, req.Temperature, req.TopP, req.MaxCompletionTokens ?? req.MaxTokens,
            chatFactory, builtIns, mcpTools);

        try
        {
            // First chunk announces the assistant role per OpenAI streaming spec.
            await WriteChunkAsync(http, serializerOptions, new OpenAiChatCompletionChunk
            {
                Id = id,
                Created = created,
                Model = model,
                Choices = new List<OpenAiChunkChoice>
                {
                    new() { Index = 0, Delta = new OpenAiResponseMessage { Role = "assistant", Content = null }, FinishReason = null },
                },
            }, ct).ConfigureAwait(false);

            ChatFinishReason? finishReason = null;
            await foreach (var update in client.GetStreamingResponseAsync(messages, options, ct).ConfigureAwait(false))
            {
                finishReason ??= update.FinishReason;
                var text = update.Text;
                if (string.IsNullOrEmpty(text)) continue;
                await WriteChunkAsync(http, serializerOptions, new OpenAiChatCompletionChunk
                {
                    Id = id,
                    Created = created,
                    Model = model,
                    Choices = new List<OpenAiChunkChoice>
                    {
                        new() { Index = 0, Delta = new OpenAiResponseMessage { Role = null!, Content = text }, FinishReason = null },
                    },
                }, ct).ConfigureAwait(false);
            }

            // Final chunk with finish_reason.
            await WriteChunkAsync(http, serializerOptions, new OpenAiChatCompletionChunk
            {
                Id = id,
                Created = created,
                Model = model,
                Choices = new List<OpenAiChunkChoice>
                {
                    new() { Index = 0, Delta = new OpenAiResponseMessage { Role = null!, Content = null }, FinishReason = MapFinishReason(finishReason) },
                },
            }, ct).ConfigureAwait(false);

            await http.Response.WriteAsync("data: [DONE]\n\n", ct).ConfigureAwait(false);
            await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var errorChunk = new { error = new { message = ex.Message, type = "upstream_error" } };
            await http.Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(errorChunk)}\n\n", ct).ConfigureAwait(false);
            await http.Response.WriteAsync("data: [DONE]\n\n", ct).ConfigureAwait(false);
            await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task WriteChunkAsync(HttpContext http, System.Text.Json.JsonSerializerOptions opts, OpenAiChatCompletionChunk chunk, CancellationToken ct)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(chunk, opts);
        await http.Response.WriteAsync($"data: {payload}\n\n", ct).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private static ChatRole MapRole(string? role) => role?.ToLowerInvariant() switch
    {
        "system" => ChatRole.System,
        "assistant" => ChatRole.Assistant,
        "tool" or "function" => ChatRole.Tool,
        _ => ChatRole.User,
    };

    private static string MapFinishReason(ChatFinishReason? reason) =>
        reason?.Value?.ToLowerInvariant() switch
        {
            "length" => "length",
            "content_filter" => "content_filter",
            "tool_calls" => "tool_calls",
            _ => "stop",
        };
}
