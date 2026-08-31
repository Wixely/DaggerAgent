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
/// can render thinking / answer / tool-call / tool-result inline as they happen. While a
/// tool call is in flight a <c>tool_progress</c> frame is written each second on top,
/// listing every running call with its elapsed time.
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
            Tools.ToolCallSink toolCalls,
            Tools.PermissionBroker permissions,
            IJobStore store,
            IOptions<OpenAIOptions> openAi,
            IOptions<EndpointsOptions> endpoints,
            CancellationToken ct) =>
        {
            await store.InitializeAsync(ct).ConfigureAwait(false);
            var model = ResolveModel(req.Model, req.EndpointId, endpoints.Value, openAi.Value);
            var state = agent.CreateState(model, req.System);
            if (!string.IsNullOrWhiteSpace(req.EndpointId)) state.EndpointId = req.EndpointId;
            if (!string.IsNullOrWhiteSpace(req.WorkingDirectory))
                state.WorkingDirectory = req.WorkingDirectory!;   // recorded for the "resume in this dir" feature
            // Carry this request's cwd as ambient per-turn context instead of mutating the shared
            // ToolsOptions singleton, which two concurrent turns would clobber. Empty = no override.
            using (Tools.ToolExecutionContext.Use(req.WorkingDirectory))
                await StreamTurnAsync(http, agent, toolCalls, permissions, state, req.Prompt, req.Images, ct).ConfigureAwait(false);
            return EmptyResult();
        });

        group.MapPost("/jobs/{id}/messages/stream", async (
            string id,
            HttpContext http,
            SendMessageStreamRequest req,
            LlmAgent agent,
            Tools.ToolCallSink toolCalls,
            Tools.PermissionBroker permissions,
            IJobStore store,
            IOptions<EndpointsOptions> endpoints,
            IOptions<OpenAIOptions> openAi,
            CancellationToken ct) =>
        {
            var state = await store.LoadAsync(id, ct).ConfigureAwait(false);
            if (state is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return EmptyResult();
            }
            if (!string.IsNullOrWhiteSpace(req.EndpointId)) state.EndpointId = req.EndpointId;
            // Per-turn model: an explicit composer override wins; an empty box means
            // "(endpoint default)" (the box placeholder), so re-resolve it from the endpoint's
            // CURRENT default each turn instead of keeping the value stamped at job creation.
            // Without this, editing an endpoint's model in the UI never reaches an open chat.
            state.Model = !string.IsNullOrWhiteSpace(req.Model)
                ? req.Model!
                : ResolveModel(null, state.EndpointId, endpoints.Value, openAi.Value);
            if (!string.IsNullOrWhiteSpace(req.WorkingDirectory))
                state.WorkingDirectory = req.WorkingDirectory!;
            using (Tools.ToolExecutionContext.Use(req.WorkingDirectory))
                await StreamTurnAsync(http, agent, toolCalls, permissions, state, req.Prompt, req.Images, ct).ConfigureAwait(false);
            return EmptyResult();
        });

        // Delivers the human's click for a permission_request frame. Not per-job: request ids
        // are unguessable and single-use, and the prompt may be answered from any open tab.
        group.MapPost("/permissions/resolve", (PermissionDecisionBody body, Tools.PermissionBroker broker) =>
        {
            if (string.IsNullOrWhiteSpace(body.RequestId)) return Results.BadRequest(new { error = "requestId required" });
            var resolved = broker.TryResolve(body.RequestId, string.IsNullOrWhiteSpace(body.OptionId) ? null : body.OptionId);
            return Results.Json(new { resolved }, JsonOpts);
        });

        return app;
    }

    private static IResult EmptyResult() => Results.Empty;

    /// <summary>
    /// Pick the model for a newly-created job. Priority: explicit request override → the
    /// model declared on the requested endpoint → the model on the global default endpoint
    /// → legacy <c>OpenAIOptions.DefaultModel</c>. Without this, a request that pins an
    /// endpoint but omits the model would silently use the legacy default (typically the
    /// local LM Studio model id), which is the wrong model for any non-OpenAI endpoint.
    /// </summary>
    internal static string ResolveModel(string? requested, string? endpointId, EndpointsOptions endpoints, OpenAIOptions legacy)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested!;

        EndpointConfig? resolved = null;
        if (!string.IsNullOrWhiteSpace(endpointId))
        {
            resolved = endpoints.Items.FirstOrDefault(e =>
                string.Equals(e.Id, endpointId, StringComparison.OrdinalIgnoreCase));
        }
        if (resolved is null && !string.IsNullOrWhiteSpace(endpoints.DefaultId))
        {
            resolved = endpoints.Items.FirstOrDefault(e =>
                string.Equals(e.Id, endpoints.DefaultId, StringComparison.OrdinalIgnoreCase));
        }

        if (resolved is not null && !string.IsNullOrWhiteSpace(resolved.DefaultModel))
            return resolved.DefaultModel;

        // For CLI-shim endpoints (Claude / Codex / Copilot), an empty model means "let the
        // CLI pick its own default" — DO NOT fall through to the legacy OpenAI model, which
        // would be a name (e.g. "qwen3.5:122b") the CLI would reject with
        // 'Model "X" from --model flag is not available.'
        if (resolved is not null && IsCliProvider(resolved.Provider))
            return "";

        return legacy.DefaultModel;
    }

    private static bool IsCliProvider(string? provider) => provider?.Trim().ToLowerInvariant() switch
    {
        "claudecli" or "claude-cli" or "codexcli" or "codex-cli" or "copilotcli" or "copilot-cli" => true,
        _ => false,
    };

    private static async Task StreamTurnAsync(
        HttpContext http,
        LlmAgent agent,
        Tools.ToolCallSink toolCalls,
        Tools.PermissionBroker permissions,
        ConversationState state,
        string prompt,
        IReadOnlyList<ImageInput>? images,
        CancellationToken clientCt)
    {
        http.Response.Headers["Content-Type"] = "text/event-stream";
        http.Response.Headers["Cache-Control"] = "no-cache";
        http.Response.Headers["Connection"] = "keep-alive";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        var sse = new SseWriter(http.Response);

        // While this stream drives the job, a delegated agent's permission request (policy
        // "ask") is put in front of the browser instead of being answered from standing
        // policy — the upstream half of the delegation proxy.
        using var permissionReg = permissions.RegisterResponder(
            state.Id, new SsePermissionResponder(sse, permissions));

        // Send job-id up front so the UI can hook up plan/pending-write polling immediately.
        await sse.WriteEventAsync("job", new { jobId = state.Id, status = state.Status.ToString(), model = state.Model }, clientCt).ConfigureAwait(false);

        var attachments = ConvertImages(images);
        var seenToolCalls = new HashSet<string>(StringComparer.Ordinal);

        // While a tool runs, the loop below is parked inside RunStreamingTurnAsync and nothing
        // reaches the client: a delegated CLI run is minutes of silence, and a proxy that drops
        // the idle connection cancels the turn - and kills the CLI with it, since the run is
        // bound to this request's token. So the tool-call sink feeds a tracker of what is in
        // flight for this job and its sub-agents, and a ticker on its own task writes a
        // tool_progress frame each second while anything is. The tracker also supplies
        // tool_result's duration from the notifier's own measurement.
        using var tracker = new InFlightToolTracker(toolCalls, state.Id);
        using var tickerCts = CancellationTokenSource.CreateLinkedTokenSource(clientCt);
        var ticker = RunProgressTickerAsync(sse, tracker, tickerCts.Token);

        // Idempotent; every exit path stops the ticker before its closing frames so no
        // progress frame can land after the status.
        async Task StopTickerAsync()
        {
            tickerCts.Cancel();
            await ticker.ConfigureAwait(false);
        }

        try
        {
            await foreach (var update in agent.RunStreamingTurnAsync(state, prompt, attachments, clientCt).ConfigureAwait(false))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextReasoningContent rc when !string.IsNullOrEmpty(rc.Text):
                            await sse.WriteEventAsync("thinking", new { text = rc.Text }, clientCt).ConfigureAwait(false);
                            break;
                        case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                            await sse.WriteEventAsync("delta", new { text = tc.Text }, clientCt).ConfigureAwait(false);
                            break;
                        case FunctionCallContent fc:
                            if (!string.IsNullOrEmpty(fc.CallId) && !seenToolCalls.Add(fc.CallId)) break;
                            await sse.WriteEventAsync("tool_call", new
                            {
                                id = fc.CallId,
                                name = fc.Name,
                                args = fc.Arguments,
                            }, clientCt).ConfigureAwait(false);
                            // Plan tool calls also fire a plan_update hint so the UI can refresh that tab.
                            if (fc.Name is "make_plan" or "update_plan")
                            {
                                await sse.WriteEventAsync("plan_update", new { jobId = state.Id }, clientCt).ConfigureAwait(false);
                            }
                            break;
                        case FunctionResultContent fr:
                            var resultText = fr.Result?.ToString() ?? "";
                            // The notifier's measurement of the whole call; null if none reached
                            // the tracker, rather than a wrong zero.
                            var durationMs = tracker.TakeCompletedMs(fr.CallId);
                            await sse.WriteEventAsync("tool_result", new
                            {
                                id = fr.CallId,
                                excerpt = resultText.Length > 1024 ? resultText[..1024] + "…(truncated)" : resultText,
                                length = resultText.Length,
                                durationMs,
                            }, clientCt).ConfigureAwait(false);
                            break;
                    }
                }
            }

            await StopTickerAsync().ConfigureAwait(false);
            await sse.WriteEventAsync("status", new
            {
                jobId = state.Id,
                status = state.Status.ToString(),
                finishReason = state.LastTurnFinishReason,
            }, clientCt).ConfigureAwait(false);
            await sse.WriteEventAsync("usage", new
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
            await StopTickerAsync().ConfigureAwait(false);
            await sse.WriteEventAsync("status", new { jobId = state.Id, status = state.Status.ToString(), cancelled = true }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await StopTickerAsync().ConfigureAwait(false);
            await sse.WriteEventAsync("error", new { message = ex.Message }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await StopTickerAsync().ConfigureAwait(false);
            await sse.WriteRawAsync("event: done\ndata: {}\n\n", CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes a <c>tool_progress</c> frame every second while any tool call is in flight, and a
    /// comment every fifteen seconds when none is, so a long model response with no visible
    /// tokens keeps the connection alive too. A second matches the counter the UI shows, and
    /// the frames are a few dozen bytes. Ends quietly on cancellation or a dead connection;
    /// the turn loop owns error reporting.
    /// </summary>
    private static async Task RunProgressTickerAsync(SseWriter sse, InFlightToolTracker tracker, CancellationToken ct)
    {
        var idleTicks = 0;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                var calls = tracker.Snapshot();
                if (calls.Count > 0)
                {
                    idleTicks = 0;
                    await sse.WriteEventAsync("tool_progress", new { calls }, ct).ConfigureAwait(false);
                }
                else if (++idleTicks >= 15)
                {
                    idleTicks = 0;
                    await sse.WriteCommentAsync("keep-alive", ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* a failed write here means the client is gone; the loop reports it */ }
    }

    /// <summary>
    /// The tool calls currently running for one streamed turn, fed by <see cref="Tools.ToolCallSink"/>.
    /// Accepts the job's own calls and, through <see cref="Tools.ToolCallEvent.ParentCallId"/>,
    /// anything running inside one of them at any depth: a sub-agent's tools arrive tagged with
    /// the <c>spawn_subagent</c> call that started them. A completed call's duration is held
    /// until its <see cref="FunctionResultContent"/> reaches the loop, which is always later,
    /// because the notifier raises Completed before the function returns to the invoking client.
    /// </summary>
    private sealed class InFlightToolTracker : IDisposable
    {
        private readonly Tools.ToolCallSink _sink;
        private readonly string _jobId;
        private readonly object _gate = new();
        private readonly Dictionary<string, (string Name, string? ParentId, long StartedAt)> _running = new(StringComparer.Ordinal);
        private readonly HashSet<string> _known = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _completedMs = new(StringComparer.Ordinal);

        public InFlightToolTracker(Tools.ToolCallSink sink, string jobId)
        {
            _sink = sink;
            _jobId = jobId;
            _sink.ToolCallStarted += OnStarted;
            _sink.ToolCallCompleted += OnCompleted;
        }

        public void Dispose()
        {
            _sink.ToolCallStarted -= OnStarted;
            _sink.ToolCallCompleted -= OnCompleted;
        }

        private void OnStarted(Tools.ToolCallEvent e)
        {
            if (e.CallId is null) return;
            lock (_gate)
            {
                var ours = e.JobId == _jobId || (e.ParentCallId is not null && _known.Contains(e.ParentCallId));
                if (!ours) return;
                _known.Add(e.CallId);
                _running[e.CallId] = (e.ToolName, e.ParentCallId, System.Diagnostics.Stopwatch.GetTimestamp());
            }
        }

        private void OnCompleted(Tools.ToolCallEvent e)
        {
            if (e.CallId is null) return;
            lock (_gate)
            {
                if (!_running.Remove(e.CallId)) return;
                _completedMs[e.CallId] = Math.Round(e.Elapsed.TotalMilliseconds, 1);
            }
        }

        public double? TakeCompletedMs(string? callId)
        {
            if (string.IsNullOrEmpty(callId)) return null;
            lock (_gate) return _completedMs.Remove(callId, out var ms) ? ms : null;
        }

        public List<object> Snapshot()
        {
            lock (_gate)
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                return _running.Select(kv => (object)new
                {
                    id = kv.Key,
                    name = kv.Value.Name,
                    parentId = kv.Value.ParentId,
                    elapsedMs = Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(kv.Value.StartedAt, now).TotalMilliseconds),
                }).ToList();
            }
        }
    }

    /// <summary>
    /// Puts a delegated agent's permission request in front of the browser as a
    /// <c>permission_request</c> frame and waits for POST /agent/permissions/resolve to deliver
    /// the click. The resolution is echoed back as <c>permission_resolved</c> so the prompt UI
    /// clears even when the decision arrived from another tab.
    /// </summary>
    private sealed class SsePermissionResponder : Tools.IPermissionResponder
    {
        private readonly SseWriter _sse;
        private readonly Tools.PermissionBroker _broker;

        public SsePermissionResponder(SseWriter sse, Tools.PermissionBroker broker)
        {
            _sse = sse;
            _broker = broker;
        }

        public async Task<string?> AskAsync(Tools.PermissionPrompt prompt, CancellationToken ct)
        {
            // Park the decision before the frame goes out so a fast click can't race the wait.
            var decision = _broker.WaitForDecisionAsync(prompt.RequestId, ct);
            await _sse.WriteEventAsync("permission_request", new
            {
                requestId = prompt.RequestId,
                agent = prompt.AgentName,
                title = prompt.Title,
                options = prompt.Options.Select(o => new { id = o.Id, name = o.Name, kind = o.Kind }),
            }, ct).ConfigureAwait(false);
            var choice = await decision.ConfigureAwait(false);
            try
            {
                await _sse.WriteEventAsync("permission_resolved",
                    new { requestId = prompt.RequestId, optionId = choice }, CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* client gone — the decision still stands */ }
            return choice;
        }
    }

    /// <summary>
    /// Serialises writes to one SSE response. The turn loop and the progress ticker both write
    /// to it, and one frame interleaved with another is two corrupt frames.
    /// </summary>
    private sealed class SseWriter
    {
        private readonly HttpResponse _response;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public SseWriter(HttpResponse response) => _response = response;

        public Task WriteEventAsync(string eventName, object payload, CancellationToken ct) =>
            WriteRawAsync($"event: {eventName}\ndata: {JsonSerializer.Serialize(payload, JsonOpts)}\n\n", ct);

        public Task WriteCommentAsync(string text, CancellationToken ct) => WriteRawAsync($": {text}\n\n", ct);

        public async Task WriteRawAsync(string frame, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _response.WriteAsync(frame, ct).ConfigureAwait(false);
                await _response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
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

}
