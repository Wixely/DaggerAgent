using System.Text;
using Daggeragent.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daggeragent.Agent;

public sealed class SubAgentManager
{
    private readonly IServiceProvider _services;
    private readonly AgentOptions _agentOptions;
    private readonly ILogger<SubAgentManager> _log;

    public SubAgentManager(
        IServiceProvider services,
        IOptions<AgentOptions> agentOptions,
        ILogger<SubAgentManager> log)
    {
        _services = services;
        _agentOptions = agentOptions.Value;
        _log = log;
    }

    public async Task<string> SpawnAsync(string? parentJobId, int depth, string task, string? modelOverride, string? parentEndpointId, string? parentModel, CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var agent = scope.ServiceProvider.GetRequiredService<LlmAgent>();

        // Inherit the parent's model unless the caller overrides it. Empty is fine — the
        // endpoint's own DefaultModel is applied at client-creation time.
        var model = !string.IsNullOrWhiteSpace(modelOverride) ? modelOverride : (parentModel ?? "");
        var systemPrompt =
            "You are a sub-agent invoked by another agent. Complete the assigned task in as few " +
            "tool calls as possible, then RETURN A SHORT FINAL ANSWER as your last assistant " +
            "message — plain text only, no further tool calls. If the task can't be done with " +
            "the tools you have, say so plainly and stop. Do not chain into another sub-agent " +
            "unless absolutely required.";

        var state = agent.CreateState(model, systemPrompt, parentJobId, depth);
        // Run the sub-agent on the SAME endpoint as its parent (not the global default) so its
        // provider, model and cost match the job that spawned it. Null/empty keeps the existing
        // "resolve the default endpoint" behaviour, matching a parent that never pinned one.
        if (!string.IsNullOrWhiteSpace(parentEndpointId)) state.EndpointId = parentEndpointId;

        var hardTimeoutSeconds = _agentOptions.SubAgentTimeoutSeconds;
        var idleTimeoutSeconds = _agentOptions.SubAgentIdleTimeoutSeconds;

        _log.LogInformation(
            "Spawning sub-agent {JobId} (depth={Depth}, parent={Parent}, hardTimeout={HardTimeout}s, idleTimeout={IdleTimeout}s)",
            state.Id, depth, parentJobId, hardTimeoutSeconds, idleTimeoutSeconds);

        // Two cancellation sources stacked:
        //   1. hardCts — bound to the parent's CT plus the absolute backstop. Stops a runaway.
        //   2. idleCts — bound to hardCts plus an idle watchdog. Fires when no streaming
        //      activity has arrived for `idleTimeoutSeconds` seconds, so a "doing nothing"
        //      sub-agent gives up fast while a "still typing" one keeps going until the backstop.
        using var hardCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (hardTimeoutSeconds > 0) hardCts.CancelAfter(TimeSpan.FromSeconds(hardTimeoutSeconds));
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(hardCts.Token);

        var lastActivityUtcTicks = DateTime.UtcNow.Ticks;
        var idleAborted = false;
        var watchdogTask = idleTimeoutSeconds > 0
            ? RunIdleWatchdog(state.Id, () => Volatile.Read(ref lastActivityUtcTicks),
                              idleTimeoutSeconds, idleCts, () => idleAborted = true)
            : Task.CompletedTask;

        var captured = new StringBuilder();
        var toolCallCount = 0;
        var textChunkCount = 0;
        var startUtc = DateTime.UtcNow;

        try
        {
            await foreach (var update in agent.RunStreamingTurnAsync(state, task, idleCts.Token).ConfigureAwait(false))
            {
                // Any update — text, thinking, tool call, tool result — resets the idle timer.
                Volatile.Write(ref lastActivityUtcTicks, DateTime.UtcNow.Ticks);

                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                            captured.Append(tc.Text);
                            textChunkCount++;
                            break;
                        case FunctionCallContent fc:
                            toolCallCount++;
                            _log.LogDebug("Sub-agent {JobId} tool call #{Count}: {Name}", state.Id, toolCallCount, fc.Name);
                            break;
                        case FunctionResultContent fr:
                            _log.LogDebug("Sub-agent {JobId} tool result callId={CallId}", state.Id, fr.CallId);
                            break;
                    }
                }
            }

            var elapsed = DateTime.UtcNow - startUtc;
            _log.LogInformation(
                "Sub-agent {JobId} completed in {Elapsed:F1}s (tools={Tools}, textChunks={Chunks}, finalChars={Chars})",
                state.Id, elapsed.TotalSeconds, toolCallCount, textChunkCount, captured.Length);

            var text = captured.ToString().Trim();
            if (text.Length == 0)
            {
                return "(sub-agent finished without producing a final answer — likely hit its tool-loop cap. " +
                       $"Did {toolCallCount} tool call(s) in {elapsed.TotalSeconds:F0}s. " +
                       "Try a smaller, more specific task.)";
            }
            return text;
        }
        catch (OperationCanceledException) when (idleAborted)
        {
            var elapsed = DateTime.UtcNow - startUtc;
            _log.LogWarning(
                "Sub-agent {JobId} aborted: idle for {Idle}s after {Elapsed:F1}s of work (tools={Tools}, partialChars={Chars})",
                state.Id, idleTimeoutSeconds, elapsed.TotalSeconds, toolCallCount, captured.Length);
            return BuildPartialResult(
                $"Error: sub-agent went idle for {idleTimeoutSeconds}s and was aborted",
                toolCallCount, elapsed, captured);
        }
        catch (OperationCanceledException) when (hardCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var elapsed = DateTime.UtcNow - startUtc;
            _log.LogWarning(
                "Sub-agent {JobId} aborted: hard timeout {Timeout}s reached (tools={Tools}, partialChars={Chars})",
                state.Id, hardTimeoutSeconds, toolCallCount, captured.Length);
            return BuildPartialResult(
                $"Error: sub-agent hit hard timeout after {hardTimeoutSeconds}s",
                toolCallCount, elapsed, captured);
        }
        catch (OperationCanceledException)
        {
            throw; // parent cancelled — propagate
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sub-agent {JobId} threw: {Message}", state.Id, ex.Message);
            return $"Error: sub-agent failed — {ex.Message}";
        }
        finally
        {
            // Stop the watchdog regardless of how we got here. It may already be cancelled.
            try { idleCts.Cancel(); } catch { /* already disposed */ }
            try { await watchdogTask.ConfigureAwait(false); } catch { /* watchdog cancellation is normal */ }
        }
    }

    private static string BuildPartialResult(string headline, int toolCalls, TimeSpan elapsed, StringBuilder captured)
    {
        var trimmed = captured.ToString().Trim();
        if (trimmed.Length == 0)
        {
            return $"{headline} after {toolCalls} tool call(s) over {elapsed.TotalSeconds:F0}s. The sub-agent produced no text output.";
        }
        // Bound the partial text to keep the parent's context window sane.
        const int max = 1500;
        var preview = trimmed.Length > max ? trimmed[..max] + "…(truncated)" : trimmed;
        return $"{headline} after {toolCalls} tool call(s) over {elapsed.TotalSeconds:F0}s. Partial output before abort: {preview}";
    }

    private async Task RunIdleWatchdog(
        string jobId,
        Func<long> readLastActivityTicks,
        int idleTimeoutSeconds,
        CancellationTokenSource idleCts,
        Action onIdle)
    {
        // Poll cadence: a quarter of the idle budget, clamped to [1s, 10s]. Frequent enough
        // to catch a stall promptly, cheap enough to ignore on the metric front.
        var pollMs = Math.Clamp(idleTimeoutSeconds * 1000 / 4, 1000, 10000);
        try
        {
            while (!idleCts.IsCancellationRequested)
            {
                await Task.Delay(pollMs, idleCts.Token).ConfigureAwait(false);
                var lastActivity = new DateTime(readLastActivityTicks(), DateTimeKind.Utc);
                var idleFor = DateTime.UtcNow - lastActivity;
                if (idleFor.TotalSeconds >= idleTimeoutSeconds)
                {
                    _log.LogWarning("Sub-agent {JobId} idle watchdog firing — silent for {Idle:F0}s", jobId, idleFor.TotalSeconds);
                    onIdle();
                    try { idleCts.Cancel(); } catch { /* already disposed */ }
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
    }
}
