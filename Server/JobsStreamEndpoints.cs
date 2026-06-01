using System.Text.Json;
using Daggeragent.Agent;
using Daggeragent.Configuration;
using Daggeragent.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Daggeragent.Server;

/// <summary>
/// SSE streaming variants of POST /agent/jobs and POST /agent/jobs/{id}/messages.
/// Powers the embedded Web UI's live transcript: each ChatResponseUpdate from
/// <see cref="LlmAgent.RunStreamingTurnAsync(ConversationState, string, IReadOnlyList{AIContent}?, CancellationToken)"/>
/// is translated into one of a small set of named SSE events so the browser
/// can render thinking / answer / tool-call / tool-result inline as they happen.
/// </summary>
public static class JobsStreamEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapJobsStream(this IEndpointRouteBuilder app, string basePath = "/agent")
    {
        var group = app.MapGroup(basePath);

        group.MapPost("/jobs/stream", async (
            HttpContext http,
            CreateJobStreamRequest req,
            LlmAgent agent,
            IJobStore store,
            IOptions<OpenAIOptions> openAi,
            IOptions<ToolsOptions> toolsOpts,
            CancellationToken ct) =>
        {
            await store.InitializeAsync(ct).ConfigureAwait(false);
            var model = string.IsNullOrWhiteSpace(req.Model) ? openAi.Value.DefaultModel : req.Model!;
            var state = agent.CreateState(model, req.System);
            if (!string.IsNullOrWhiteSpace(req.EndpointId)) state.EndpointId = req.EndpointId;
            if (!string.IsNullOrWhiteSpace(req.WorkingDirectory))
            {
                state.WorkingDirectory = req.WorkingDirectory!;
                // The agent's filesystem tools read from ToolsOptions.WorkingDirectory each call —
                // mirror it so this turn's tools land where the UI expects.
                toolsOpts.Value.WorkingDirectory = req.WorkingDirectory!;
            }
            await StreamTurnAsync(http, agent, state, req.Prompt, req.Images, ct).ConfigureAwait(false);
            return EmptyResult();
        });

        group.MapPost("/jobs/{id}/messages/stream", async (
            string id,
            HttpContext http,
            SendMessageStreamRequest req,
            LlmAgent agent,
            IJobStore store,
            IOptions<ToolsOptions> toolsOpts,
            CancellationToken ct) =>
        {
            var state = await store.LoadAsync(id, ct).ConfigureAwait(false);
            if (state is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return EmptyResult();
            }
            if (!string.IsNullOrWhiteSpace(req.Model)) state.Model = req.Model!;
            if (!string.IsNullOrWhiteSpace(req.EndpointId)) state.EndpointId = req.EndpointId;
            if (!string.IsNullOrWhiteSpace(req.WorkingDirectory))
            {
                state.WorkingDirectory = req.WorkingDirectory!;
                // Mirror onto ToolsOptions so the filesystem tools jail follows the UI's cwd.
                toolsOpts.Value.WorkingDirectory = req.WorkingDirectory!;
            }
            await StreamTurnAsync(http, agent, state, req.Prompt, req.Images, ct).ConfigureAwait(false);
            return EmptyResult();
        });

        return app;
    }

    private static IResult EmptyResult() => Results.Empty;

    private static async Task StreamTurnAsync(
        HttpContext http,
        LlmAgent agent,
        ConversationState state,
        string prompt,
        IReadOnlyList<ImageInput>? images,
        CancellationToken clientCt)
    {
        http.Response.Headers["Content-Type"] = "text/event-stream";
        http.Response.Headers["Cache-Control"] = "no-cache";
        http.Response.Headers["Connection"] = "keep-alive";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        // Send job-id up front so the UI can hook up plan/pending-write polling immediately.
        await WriteEventAsync(http, "job", new { jobId = state.Id, status = state.Status.ToString(), model = state.Model }, clientCt).ConfigureAwait(false);

        var attachments = ConvertImages(images);
        var seenToolCalls = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            await foreach (var update in agent.RunStreamingTurnAsync(state, prompt, attachments, clientCt).ConfigureAwait(false))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextReasoningContent rc when !string.IsNullOrEmpty(rc.Text):
                            await WriteEventAsync(http, "thinking", new { text = rc.Text }, clientCt).ConfigureAwait(false);
                            break;
                        case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                            await WriteEventAsync(http, "delta", new { text = tc.Text }, clientCt).ConfigureAwait(false);
                            break;
                        case FunctionCallContent fc:
                            if (!string.IsNullOrEmpty(fc.CallId) && !seenToolCalls.Add(fc.CallId)) break;
                            await WriteEventAsync(http, "tool_call", new
                            {
                                id = fc.CallId,
                                name = fc.Name,
                                args = fc.Arguments,
                            }, clientCt).ConfigureAwait(false);
                            // Plan tool calls also fire a plan_update hint so the UI can refresh that tab.
                            if (fc.Name is "make_plan" or "update_plan")
                            {
                                await WriteEventAsync(http, "plan_update", new { jobId = state.Id }, clientCt).ConfigureAwait(false);
                            }
                            break;
                        case FunctionResultContent fr:
                            var resultText = fr.Result?.ToString() ?? "";
                            await WriteEventAsync(http, "tool_result", new
                            {
                                id = fr.CallId,
                                excerpt = resultText.Length > 1024 ? resultText[..1024] + "…(truncated)" : resultText,
                                length = resultText.Length,
                            }, clientCt).ConfigureAwait(false);
                            break;
                    }
                }
            }

            await WriteEventAsync(http, "status", new
            {
                jobId = state.Id,
                status = state.Status.ToString(),
                finishReason = state.LastTurnFinishReason,
            }, clientCt).ConfigureAwait(false);
            await WriteEventAsync(http, "usage", new
            {
                inputTokens = state.TotalInputTokens,
                outputTokens = state.TotalOutputTokens,
                thinkingTokens = state.TotalThinkingTokens,
                costUsd = state.TotalCostUsd,
                approxTokenCount = state.ApproxTokenCount,
                turnsTaken = state.TurnsTaken,
            }, clientCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WriteEventAsync(http, "status", new { jobId = state.Id, status = state.Status.ToString(), cancelled = true }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteEventAsync(http, "error", new { message = ex.Message }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await http.Response.WriteAsync("event: done\ndata: {}\n\n", CancellationToken.None).ConfigureAwait(false);
            await http.Response.Body.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<AIContent>? ConvertImages(IReadOnlyList<ImageInput>? images)
    {
        if (images is null || images.Count == 0) return null;
        var list = new List<AIContent>(images.Count);
        foreach (var img in images)
        {
            if (string.IsNullOrWhiteSpace(img.Base64) || string.IsNullOrWhiteSpace(img.MediaType)) continue;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(img.Base64); }
            catch (FormatException) { continue; }
            list.Add(new DataContent(bytes, img.MediaType));
        }
        return list.Count == 0 ? null : list;
    }

    private static async Task WriteEventAsync(HttpContext http, string eventName, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        await http.Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", ct).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
    }
}
