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

public static class OllamaCompatEndpoints
{
    public static IEndpointRouteBuilder MapOllamaCompatApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/version", () => Results.Ok(new OllamaVersionResponse()));

        app.MapGet("/api/tags", (IOptions<AgentOptions> agentOpts, IOptions<OpenAIOptions> openAiOpts) =>
        {
            var model = ChatCompatHelper.ResolveModel(null, openAiOpts.Value);
            return Results.Ok(new OllamaTagsResponse
            {
                Models = new List<OllamaTagsModel>
                {
                    new()
                    {
                        Name = model,
                        Model = model,
                        ModifiedAt = DateTimeOffset.UtcNow.ToString("O"),
                        Digest = "daggeragent",
                    },
                },
            });
        });

        app.MapPost("/api/show", (OllamaShowRequest req, IOptions<AgentOptions> agentOpts, IOptions<OpenAIOptions> openAiOpts) =>
        {
            var configured = ChatCompatHelper.ResolveModel(null, openAiOpts.Value);
            var requested = req.Model ?? req.Name;
            if (!string.IsNullOrEmpty(requested) && !string.Equals(requested, configured, StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound(new { error = $"model '{requested}' not found" });
            }
            return Results.Ok(new OllamaShowResponse
            {
                Modelfile = $"# DaggerAgent proxy for {configured}\nFROM {configured}",
                Parameters = "",
                Template = "{{ .Prompt }}",
            });
        });

        app.MapPost("/api/chat", async (
            HttpContext httpContext,
            OllamaChatRequest req,
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
                .Select(m => new ChatMessage(MapRole(m.Role), m.Content ?? ""))
                .ToList();

            if (messages.Count == 0)
            {
                return Results.BadRequest(new { error = "messages array is empty." });
            }

            // Ollama defaults stream:true if the field is missing. Match that default.
            if (req.Stream != false)
            {
                await StreamChatAsync(httpContext, messages, model, req, chatFactory, builtIns, mcpTools, ct).ConfigureAwait(false);
                return Results.Empty;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await ChatCompatHelper.RunOneShotAsync(
                messages, model,
                req.Options?.Temperature, req.Options?.TopP, req.Options?.NumPredict,
                chatFactory, builtIns, mcpTools, ct).ConfigureAwait(false);
            sw.Stop();

            if (result.Response is null)
            {
                return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status502BadGateway);
            }

            var totalNanos = sw.Elapsed.Ticks * 100;
            return Results.Ok(new OllamaChatResponse
            {
                Model = model,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                Message = new OllamaMessage { Role = "assistant", Content = result.Response.Text ?? "" },
                Done = true,
                DoneReason = MapFinishReason(result.Response.FinishReason),
                TotalDuration = totalNanos,
                PromptEvalCount = result.Response.Usage?.InputTokenCount ?? 0,
                EvalCount = result.Response.Usage?.OutputTokenCount ?? 0,
                EvalDuration = totalNanos,
            });
        });

        app.MapPost("/api/generate", async (
            HttpContext httpContext,
            OllamaGenerateRequest req,
            ChatClientFactory chatFactory,
            BuiltInToolRegistry builtIns,
            McpToolProvider mcpTools,
            IOptions<AgentOptions> agentOpts,
            IOptions<OpenAIOptions> openAiOpts,
            CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(req.Prompt))
            {
                return Results.BadRequest(new { error = "prompt is required." });
            }

            var model = ChatCompatHelper.ResolveModel(req.Model, openAiOpts.Value);

            var messages = new List<ChatMessage>();
            if (!string.IsNullOrEmpty(req.System)) messages.Add(new ChatMessage(ChatRole.System, req.System!));
            messages.Add(new ChatMessage(ChatRole.User, req.Prompt!));

            if (req.Stream != false)
            {
                await StreamGenerateAsync(httpContext, messages, model, req, chatFactory, builtIns, mcpTools, ct).ConfigureAwait(false);
                return Results.Empty;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await ChatCompatHelper.RunOneShotAsync(
                messages, model,
                req.Options?.Temperature, req.Options?.TopP, req.Options?.NumPredict,
                chatFactory, builtIns, mcpTools, ct).ConfigureAwait(false);
            sw.Stop();

            if (result.Response is null)
            {
                return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status502BadGateway);
            }

            var totalNanos = sw.Elapsed.Ticks * 100;
            return Results.Ok(new OllamaGenerateResponse
            {
                Model = model,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                Response = result.Response.Text ?? "",
                Done = true,
                DoneReason = MapFinishReason(result.Response.FinishReason),
                TotalDuration = totalNanos,
                PromptEvalCount = result.Response.Usage?.InputTokenCount ?? 0,
                EvalCount = result.Response.Usage?.OutputTokenCount ?? 0,
                EvalDuration = totalNanos,
            });
        });

        // Model-management endpoints — DaggerAgent proxies to an upstream LLM and does not host
        // model files, so these are intentionally unsupported.
        IResult NotSupported(string what) => Results.Problem(
            detail: $"{what} is not supported — DaggerAgent does not host model files; configure the upstream OpenAI-protocol endpoint instead.",
            statusCode: StatusCodes.Status501NotImplemented);

        app.MapPost("/api/pull", () => NotSupported("/api/pull"));
        app.MapPost("/api/push", () => NotSupported("/api/push"));
        app.MapPost("/api/create", () => NotSupported("/api/create"));
        app.MapPost("/api/copy", () => NotSupported("/api/copy"));
        app.MapDelete("/api/delete", () => NotSupported("/api/delete"));
        app.MapPost("/api/embeddings", () => NotSupported("/api/embeddings"));
        app.MapPost("/api/embed", () => NotSupported("/api/embed"));

        return app;
    }

    private static async Task StreamChatAsync(
        HttpContext http,
        IList<ChatMessage> messages,
        string model,
        OllamaChatRequest req,
        ChatClientFactory chatFactory,
        BuiltInToolRegistry builtIns,
        McpToolProvider mcpTools,
        CancellationToken ct)
    {
        http.Response.Headers["Content-Type"] = "application/x-ndjson";
        var (client, options) = ChatCompatHelper.BuildClientAndOptions(
            model, req.Options?.Temperature, req.Options?.TopP, req.Options?.NumPredict,
            chatFactory, builtIns, mcpTools);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ChatFinishReason? finishReason = null;
            await foreach (var update in client.GetStreamingResponseAsync(messages, options, ct).ConfigureAwait(false))
            {
                finishReason ??= update.FinishReason;
                var text = update.Text;
                if (string.IsNullOrEmpty(text)) continue;
                await WriteLineAsync(http, new OllamaChatResponse
                {
                    Model = model,
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    Message = new OllamaMessage { Role = "assistant", Content = text },
                    Done = false,
                }, ct).ConfigureAwait(false);
            }

            sw.Stop();
            var totalNanos = sw.Elapsed.Ticks * 100;
            await WriteLineAsync(http, new OllamaChatResponse
            {
                Model = model,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                Message = new OllamaMessage { Role = "assistant", Content = "" },
                Done = true,
                DoneReason = MapFinishReason(finishReason),
                TotalDuration = totalNanos,
                EvalDuration = totalNanos,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteLineAsync(http, new { error = ex.Message }, ct).ConfigureAwait(false);
        }
    }

    private static async Task StreamGenerateAsync(
        HttpContext http,
        IList<ChatMessage> messages,
        string model,
        OllamaGenerateRequest req,
        ChatClientFactory chatFactory,
        BuiltInToolRegistry builtIns,
        McpToolProvider mcpTools,
        CancellationToken ct)
    {
        http.Response.Headers["Content-Type"] = "application/x-ndjson";
        var (client, options) = ChatCompatHelper.BuildClientAndOptions(
            model, req.Options?.Temperature, req.Options?.TopP, req.Options?.NumPredict,
            chatFactory, builtIns, mcpTools);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ChatFinishReason? finishReason = null;
            await foreach (var update in client.GetStreamingResponseAsync(messages, options, ct).ConfigureAwait(false))
            {
                finishReason ??= update.FinishReason;
                var text = update.Text;
                if (string.IsNullOrEmpty(text)) continue;
                await WriteLineAsync(http, new OllamaGenerateResponse
                {
                    Model = model,
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    Response = text,
                    Done = false,
                }, ct).ConfigureAwait(false);
            }

            sw.Stop();
            var totalNanos = sw.Elapsed.Ticks * 100;
            await WriteLineAsync(http, new OllamaGenerateResponse
            {
                Model = model,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                Response = "",
                Done = true,
                DoneReason = MapFinishReason(finishReason),
                TotalDuration = totalNanos,
                EvalDuration = totalNanos,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteLineAsync(http, new { error = ex.Message }, ct).ConfigureAwait(false);
        }
    }

    private static async Task WriteLineAsync(HttpContext http, object payload, CancellationToken ct)
    {
        await http.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(payload) + "\n", ct).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private static ChatRole MapRole(string? role) => role?.ToLowerInvariant() switch
    {
        "system" => ChatRole.System,
        "assistant" => ChatRole.Assistant,
        "tool" => ChatRole.Tool,
        _ => ChatRole.User,
    };

    private static string MapFinishReason(ChatFinishReason? reason) =>
        reason?.Value?.ToLowerInvariant() switch
        {
            "length" => "length",
            "tool_calls" => "tool_calls",
            _ => "stop",
        };
}
