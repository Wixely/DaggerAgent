using Daggeragent.Configuration;
using Daggeragent.Llm;
using Daggeragent.Mcp;
using Daggeragent.Persistence;
using Daggeragent.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daggeragent.Agent;

public sealed class LlmAgent
{
    private readonly ChatClientFactory _chatClientFactory;
    private readonly BuiltInToolRegistry _builtIns;
    private readonly McpToolProvider _mcpTools;
    private readonly IJobStore _jobStore;
    private readonly ContextCompressor _compressor;
    private readonly TokenEstimator _tokenEstimator;
    private readonly PersonalityProvider _personality;
    private readonly AgentOptions _agentOptions;
    private readonly ToolsOptions _toolsOptions;
    private readonly PricingOptions _pricingOptions;
    private readonly HostLaunchInfo _launchInfo;
    private readonly ToolResultStore _toolResultStore;
    private readonly ILogger<LlmAgent> _log;

    public LlmAgent(
        ChatClientFactory chatClientFactory,
        BuiltInToolRegistry builtIns,
        McpToolProvider mcpTools,
        IJobStore jobStore,
        ContextCompressor compressor,
        TokenEstimator tokenEstimator,
        PersonalityProvider personality,
        IOptions<AgentOptions> agentOptions,
        IOptions<ToolsOptions> toolsOptions,
        IOptions<PricingOptions> pricingOptions,
        HostLaunchInfo launchInfo,
        ToolResultStore toolResultStore,
        ILogger<LlmAgent> log)
    {
        _chatClientFactory = chatClientFactory;
        _builtIns = builtIns;
        _mcpTools = mcpTools;
        _jobStore = jobStore;
        _compressor = compressor;
        _tokenEstimator = tokenEstimator;
        _personality = personality;
        _agentOptions = agentOptions.Value;
        _toolsOptions = toolsOptions.Value;
        _pricingOptions = pricingOptions.Value;
        _launchInfo = launchInfo;
        _toolResultStore = toolResultStore;
        _log = log;
    }

    /// <summary>
    /// Apply the standard per-turn tool decorator stack: caching (loop detection +
    /// memoise within a turn), then offloading (stash oversized string results into
    /// <see cref="ToolResultStore"/>). The offloader is skipped for the
    /// <c>read_tool_result</c> / <c>head_tool_result</c> / ... consumer tools so reading
    /// a 16K slice doesn't recursively offload its own response.
    /// </summary>
    private List<AITool> WrapTools(IEnumerable<AITool> raw, string jobId, Tools.TurnToolCache cache)
    {
        var threshold = _toolsOptions.MaxToolResultChars;
        var wrapped = new List<AITool>();
        foreach (var t in raw)
        {
            if (t is not AIFunction f) { wrapped.Add(t); continue; }
            AIFunction outer = new Tools.CachingAIFunction(f, cache);
            if (threshold > 0 && !Tools.ToolResultTools.ConsumerToolNames.Contains(f.Name))
                outer = new Tools.OffloadingAIFunction(outer, _toolResultStore, jobId, threshold);
            wrapped.Add(outer);
        }
        return wrapped;
    }

    public ConversationState CreateState(string model, string? systemPrompt = null, string? parentJobId = null, int depth = 0)
    {
        var basePrompt = systemPrompt ?? _agentOptions.SystemPrompt;
        var personality = _personality.LoadCurrent();
        var fullPrompt = string.IsNullOrEmpty(personality)
            ? basePrompt
            : $"{basePrompt}\n\n# Project context (from {_agentOptions.PersonalityFile})\n{personality}";

        if (_agentOptions.IncludeRuntimeContext)
        {
            fullPrompt += "\n\n" + BuildRuntimeContext();
        }

        if (_toolsOptions.ForcePlan)
        {
            fullPrompt += "\n\n# Planning\n" +
                          "Before reaching for any filesystem, shell, web, or memory tool, call `make_plan` " +
                          "with an ordered list of short, concrete steps. Keep the plan current by calling " +
                          "`update_plan` when a step starts (in_progress), finishes (done), or hits a blocker " +
                          "(blocked). For trivial one-step requests a single-step plan is fine. The plan keeps " +
                          "you on track and prevents looping on the same tool calls.";
        }

        var state = new ConversationState
        {
            Model = model,
            SystemPrompt = fullPrompt,
            ParentId = parentJobId,
            Depth = depth,
            Status = JobStatus.Pending,
            WorkingDirectory = _launchInfo.OriginalWorkingDirectory,
        };
        state.History.Add(new ChatMessage(ChatRole.System, fullPrompt));
        return state;
    }

    public async Task<ChatResponse> RunTurnAsync(ConversationState state, string userMessage, CancellationToken cancellationToken = default)
    {
        state.History.Add(new ChatMessage(ChatRole.User, userMessage));
        state.Status = JobStatus.Running;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _jobStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);

        var rawTools = new List<AITool>();
        rawTools.AddRange(_builtIns.ForAgent(state.Id, state.Depth));
        rawTools.AddRange(_mcpTools.GetTools());
        rawTools = RouteTools(rawTools, userMessage, state);

        var cache = new Tools.TurnToolCache();
        var tools = WrapTools(rawTools, state.Id, cache);

        var options = new ChatOptions
        {
            ModelId = state.Model,
            Tools = tools.Count > 0 ? tools : null,
        };

        // Sub-agents get a tighter per-request iteration cap than top-level turns so a
        // confused sub-agent surrenders earlier instead of holding the parent's tool call open.
        var iterationCap = state.Depth > 0
            ? _agentOptions.MaxTurnsPerSubAgent
            : _agentOptions.MaxTurnsPerInvocation;
        var client = _chatClientFactory.Create(state.Model, iterationCap, state.EndpointId, state.Id);

        _log.LogDebug("Running turn for job {JobId} (depth={Depth}, tools={ToolCount}, model={Model}, iterCap={IterCap})",
            state.Id, state.Depth, tools.Count, state.Model, iterationCap);

        ChatResponse response;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            response = await client.GetResponseAsync(state.History, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "LLM call failed for job {JobId}", state.Id);
            state.Status = JobStatus.Failed;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            await _jobStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally { sw.Stop(); }

        var thinkingTokensThisTurn = 0L;
        foreach (var msg in response.Messages)
        {
            state.History.Add(_agentOptions.HideThinkingFromHistory
                ? StripThinkingFromMessage(msg, ref thinkingTokensThisTurn)
                : msg);
        }
        state.TotalThinkingTokens += thinkingTokensThisTurn;

        state.TurnsTaken++;
        state.ApproxTokenCount = _tokenEstimator.Estimate(state.History);
        RecordUsage(state, response, thinkingTokensThisTurn, sw.Elapsed, streaming: false);
        LogCacheStats(state, cache);

        if (state.ApproxTokenCount > _agentOptions.CompressionThreshold)
        {
            await _compressor.CompressAsync(state, cancellationToken).ConfigureAwait(false);
        }

        state.Status = JobStatus.Paused;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _jobStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);

        return response;
    }

    public IAsyncEnumerable<ChatResponseUpdate> RunStreamingTurnAsync(
        ConversationState state,
        string userMessage,
        CancellationToken cancellationToken = default)
        => RunStreamingTurnAsync(state, userMessage, attachments: null, cancellationToken);

    /// <summary>
    /// Overload that accepts non-text user content (e.g. images via <see cref="DataContent"/>)
    /// alongside the text prompt. Used by the web UI to send multimodal turns.
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> RunStreamingTurnAsync(
        ConversationState state,
        string userMessage,
        IReadOnlyList<AIContent>? attachments,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (attachments is { Count: > 0 })
        {
            var parts = new List<AIContent> { new TextContent(userMessage) };
            parts.AddRange(attachments);
            state.History.Add(new ChatMessage(ChatRole.User, parts));
        }
        else
        {
            state.History.Add(new ChatMessage(ChatRole.User, userMessage));
        }
        state.Status = JobStatus.Running;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _jobStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);

        var rawTools = new List<AITool>();
        rawTools.AddRange(_builtIns.ForAgent(state.Id, state.Depth));
        rawTools.AddRange(_mcpTools.GetTools());
        rawTools = RouteTools(rawTools, userMessage, state);

        var cache = new Tools.TurnToolCache();
        var tools = WrapTools(rawTools, state.Id, cache);

        var options = new ChatOptions
        {
            ModelId = state.Model,
            Tools = tools.Count > 0 ? tools : null,
        };

        var iterationCap = state.Depth > 0
            ? _agentOptions.MaxTurnsPerSubAgent
            : _agentOptions.MaxTurnsPerInvocation;
        var client = _chatClientFactory.Create(state.Model, iterationCap, state.EndpointId, state.Id);
        var collected = new List<ChatResponseUpdate>();

        // Tool-time accounting: we want the per-turn tokens-per-second to reflect raw
        // LLM generation speed, not get diluted by slow tool calls. Each FunctionCall
        // update opens a timed window, the matching FunctionResult closes it; the sum
        // is subtracted from total elapsed when reporting tkps.
        var toolStarts = new Dictionary<string, TimeSpan>();
        var toolElapsed = TimeSpan.Zero;
        var toolCalls = 0;

        // try/finally so partial state is always paired up if the consumer cancels
        // mid-stream. Without this the history ends with the user message and the
        // assistant turn never closes — the next user message then arrives as a second
        // consecutive user turn, which most chat APIs reject or treat as a continuation.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await foreach (var update in client.GetStreamingResponseAsync(state.History, options, cancellationToken).ConfigureAwait(false))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case FunctionCallContent fc:
                            toolCalls++;
                            if (!string.IsNullOrEmpty(fc.CallId)) toolStarts[fc.CallId] = sw.Elapsed;
                            break;
                        case FunctionResultContent fr:
                            if (!string.IsNullOrEmpty(fr.CallId) && toolStarts.Remove(fr.CallId, out var start))
                                toolElapsed += sw.Elapsed - start;
                            break;
                    }
                }
                collected.Add(update);
                yield return update;
            }
        }
        finally
        {
            sw.Stop();
            await FinaliseStreamingTurnAsync(state, collected, cache, sw.Elapsed, toolElapsed, toolCalls).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Pair the user message we appended at turn start with whatever assistant content
    /// the model managed to stream before cancellation / completion. Always called via
    /// the streaming turn's finally so the history can't be left mid-turn. Uses
    /// CancellationToken.None for the save because we're already past the cancel point
    /// and just need to persist state.
    /// </summary>
    private async Task FinaliseStreamingTurnAsync(ConversationState state, List<ChatResponseUpdate> collected, Tools.TurnToolCache cache, TimeSpan elapsed, TimeSpan toolElapsed, int toolCalls)
    {
        var response = collected.ToChatResponse();
        var thinkingTokensThisTurn = 0L;
        foreach (var msg in response.Messages)
        {
            state.History.Add(_agentOptions.HideThinkingFromHistory
                ? StripThinkingFromMessage(msg, ref thinkingTokensThisTurn)
                : msg);
        }
        state.TotalThinkingTokens += thinkingTokensThisTurn;

        // If the last history entry is still the user message we appended at turn
        // start, the model never replied. Add a synthetic assistant marker so the
        // conversation is well-formed for the next call.
        if (state.History.Count > 0 && state.History[^1].Role == ChatRole.User)
        {
            state.History.Add(new ChatMessage(ChatRole.Assistant,
                "[turn cancelled by user before assistant responded]"));
        }

        state.TurnsTaken++;
        state.ApproxTokenCount = _tokenEstimator.Estimate(state.History);
        RecordUsage(state, response, thinkingTokensThisTurn, elapsed, streaming: true);

        // Tool stats for the stats line: LLM-only elapsed = total - sum of tool windows.
        // Clamp at 0 (paranoia: monotonic stopwatch shouldn't underflow but just in case).
        var llmElapsed = elapsed - toolElapsed;
        if (llmElapsed < TimeSpan.Zero) llmElapsed = TimeSpan.Zero;
        state.LastTurnLlmElapsedMs = (long)llmElapsed.TotalMilliseconds;
        state.LastTurnToolCalls = toolCalls;
        // FinishReason is "stop" for a clean EOS, "length" when max_tokens was hit, or
        // null/missing when the server didn't supply one (usually means the stream was
        // cut short). InteractiveRunner reads this and may auto-continue.
        state.LastTurnFinishReason = response.FinishReason?.Value;

        LogCacheStats(state, cache);
        if (state.ApproxTokenCount > _agentOptions.CompressionThreshold)
        {
            try { await _compressor.CompressAsync(state, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "Compression failed during streaming turn cleanup"); }
        }

        state.Status = JobStatus.Paused;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        try { await _jobStore.SaveAsync(state, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogWarning(ex, "Job save failed during streaming turn cleanup"); }
    }

    /// <summary>
    /// Build a compact "Runtime context" block describing the host the agent is running on:
    /// OS family + version, kernel/distro string, architecture, .NET runtime version,
    /// hostname, working directory, path separator, and the shell the agent should default
    /// to. Saves the agent from guessing the platform (which small models often get wrong,
    /// reaching for bash on Windows or backslash paths on Linux).
    /// </summary>
    private string BuildRuntimeContext()
    {
        var os = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "Windows"
               : System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux) ? "Linux"
               : System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX) ? "macOS"
               : "Other";

        var shellHint = os switch
        {
            "Windows" => "PowerShell (use `pwsh` or `powershell.exe`; `;` chains commands, `&&` does not work in Windows PowerShell 5.1)",
            "Linux"   => "bash (POSIX shell)",
            "macOS"   => "zsh (POSIX shell)",
            _         => "bash",
        };

        var pathSep = Path.DirectorySeparatorChar;
        var lineEnding = os == "Windows" ? @"\r\n (CRLF)" : @"\n (LF)";
        var workingDir = string.IsNullOrWhiteSpace(_toolsOptions.WorkingDirectory)
            ? _launchInfo.OriginalWorkingDirectory
            : (Path.IsPathRooted(_toolsOptions.WorkingDirectory)
                ? Path.GetFullPath(_toolsOptions.WorkingDirectory)
                : Path.GetFullPath(Path.Combine(_launchInfo.OriginalWorkingDirectory, _toolsOptions.WorkingDirectory)));

        return $"# Runtime context\n" +
               $"- os: {os} ({System.Runtime.InteropServices.RuntimeInformation.OSDescription})\n" +
               $"- architecture: {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}\n" +
               $"- runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}\n" +
               $"- working_directory: {workingDir}\n" +
               $"- path_separator: '{pathSep}'\n" +
               $"- line_endings: {lineEnding}\n" +
               $"- default_shell: {shellHint}\n" +
               $"- current_date_utc: {DateTimeOffset.UtcNow:yyyy-MM-dd}\n" +
               "Use platform-appropriate paths and shell syntax when calling tools.";
    }

    /// <summary>
    /// When GranularTools=true, the full tool list is handed to the model untouched.
    /// When GranularTools=false (default), <see cref="Tools.ToolCategorizer"/> picks the
    /// categories that look relevant to the user's message and filters the list — small
    /// models get a focused ~5-10 tool surface instead of all ~25.
    /// </summary>
    private List<AITool> RouteTools(List<AITool> rawTools, string userMessage, ConversationState state)
    {
        if (_toolsOptions.GranularTools) return rawTools;

        var enabled = Tools.ToolCategorizer.Route(userMessage, includePlan: _toolsOptions.ForcePlan);
        var filtered = Tools.ToolCategorizer.Filter(rawTools, enabled);

        _log.LogDebug("Tool routing for job {JobId} turn {Turn}: {Before} -> {After} tools (categories: {Categories})",
            state.Id, state.TurnsTaken + 1, rawTools.Count, filtered.Count, string.Join(",", enabled));

        // Guard: if filtering wiped everything (extreme edge case — bad keyword map +
        // GranularTools off), fall back to the unfiltered list so the agent isn't stranded.
        return filtered.Count == 0 ? rawTools : filtered;
    }

    private void LogCacheStats(ConversationState state, Tools.TurnToolCache cache)
    {
        if (cache.HitCount + cache.MissCount == 0) return;
        _log.LogDebug("Tool cache for job {JobId} turn {Turn}: {Hits} hits, {Misses} misses, {Entries} entries",
            state.Id, state.TurnsTaken, cache.HitCount, cache.MissCount, cache.EntryCount);
    }

    private void RecordUsage(ConversationState state, ChatResponse response, long thinkingTokens, TimeSpan elapsed, bool streaming)
    {
        var usage = response.Usage;
        var inTok = usage?.InputTokenCount ?? 0;
        var outTok = usage?.OutputTokenCount ?? 0;
        var totalTok = usage?.TotalTokenCount ?? (inTok + outTok);

        state.TotalInputTokens += inTok;
        state.TotalOutputTokens += outTok;
        state.LastTurnElapsedMs = (long)elapsed.TotalMilliseconds;
        state.LastTurnTotalTokens = totalTok;
        state.LastTurnOutputTokens = outTok;

        decimal turnCost = 0m;
        if (_pricingOptions.Models.TryGetValue(state.Model, out var price))
        {
            turnCost = (inTok / 1_000_000m) * price.InputPerMillion
                     + (outTok / 1_000_000m) * price.OutputPerMillion;
            state.TotalCostUsd += turnCost;
        }

        // Verbose mode = INF (visible on stdout in interactive); otherwise Debug (file only).
        var level = _agentOptions.VerboseTurnStats
            ? Microsoft.Extensions.Logging.LogLevel.Information
            : Microsoft.Extensions.Logging.LogLevel.Debug;

        _log.Log(level,
            "LLM turn complete: job={JobId} depth={Depth} model={Model} streaming={Streaming} turn={Turn} " +
            "input_tokens={InputTokens} output_tokens={OutputTokens} thinking_tokens={ThinkingTokens} total_tokens={TotalTokens} " +
            "elapsed_ms={ElapsedMs} " +
            "cumulative_input={CumulativeInput} cumulative_output={CumulativeOutput} cumulative_thinking={CumulativeThinking} " +
            "turn_cost_usd={TurnCostUsd:F6} cumulative_cost_usd={CumulativeCostUsd:F6}",
            state.Id, state.Depth, state.Model, streaming, state.TurnsTaken,
            inTok, outTok, thinkingTokens, totalTok,
            state.LastTurnElapsedMs,
            state.TotalInputTokens, state.TotalOutputTokens, state.TotalThinkingTokens,
            turnCost, state.TotalCostUsd);
    }

    private ChatMessage StripThinkingFromMessage(ChatMessage msg, ref long thinkingTokens)
    {
        // Fast path: messages with no text and no TextContent (e.g. tool-call-only) pass through.
        var anyText = !string.IsNullOrEmpty(msg.Text) || msg.Contents.OfType<TextContent>().Any(t => !string.IsNullOrEmpty(t.Text));
        if (!anyText) return msg;

        if (msg.Contents.Count == 0)
        {
            var text = msg.Text ?? "";
            var thinking = ThinkingSplitter.ExtractThinking(text);
            if (thinking.Length == 0) return msg;
            thinkingTokens += _tokenEstimator.CountTokens(thinking);
            return new ChatMessage(msg.Role, ThinkingSplitter.StripThinking(text))
            {
                AuthorName = msg.AuthorName,
                MessageId = msg.MessageId,
            };
        }

        var anyChange = false;
        var newContents = new List<AIContent>(msg.Contents.Count);
        foreach (var c in msg.Contents)
        {
            // MEAI's canonical reasoning content — emitted by providers that parse <think>
            // server-side (LM Studio's reasoning mode, DeepSeek-R1, etc.). Always treated as
            // thinking, regardless of whether the inner text has <think> tags.
            if (c is TextReasoningContent rc)
            {
                if (!string.IsNullOrEmpty(rc.Text))
                    thinkingTokens += _tokenEstimator.CountTokens(rc.Text);
                anyChange = true;
                continue;
            }
            if (c is TextContent tc && !string.IsNullOrEmpty(tc.Text))
            {
                var thinking = ThinkingSplitter.ExtractThinking(tc.Text);
                if (thinking.Length > 0)
                {
                    thinkingTokens += _tokenEstimator.CountTokens(thinking);
                    var stripped = ThinkingSplitter.StripThinking(tc.Text);
                    if (!string.IsNullOrEmpty(stripped)) newContents.Add(new TextContent(stripped));
                    anyChange = true;
                    continue;
                }
            }
            newContents.Add(c);
        }
        if (!anyChange) return msg;

        return new ChatMessage(msg.Role, newContents)
        {
            AuthorName = msg.AuthorName,
            MessageId = msg.MessageId,
        };
    }
}
