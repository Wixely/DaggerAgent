using System.Text;
using System.Threading.Channels;
using Daggeragent.Agent;
using Daggeragent.Configuration;
using Daggeragent.Mcp;
using Daggeragent.Persistence;
using Daggeragent.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Daggeragent.Modes;

/// <summary>
/// Full-screen TUI for interactive mode. The whole terminal is wrapped in a single
/// rounded Spectre.Console panel; inside it a three-row Layout hosts a header, the
/// scrolling chat region (agent output + tool calls), and the input editor at the
/// bottom. AnsiConsole.Live drives the render loop, so the frame auto-fits and
/// re-fits the terminal on resize.
///
/// Spectre's TextPrompt is line-only and missing history / Ctrl+W / F2 / F3, so the
/// input editor is hand-rolled: Console.ReadKey reads each keystroke, mutates the
/// input buffer, and the buffer is re-rendered into the input region with a faked
/// cursor block. The real terminal cursor is hidden by Live.
/// </summary>
public sealed class InteractiveRunner
{
    private const int HistoryCap = 100;
    private const int ChatScrollback = 500;        // hard cap on retained chat lines
    private const string CursorGlyph = "▌";        // shown at the logical input cursor

    // Limits how many times we'll auto-retry a turn when the model emits a tool call as
    // plain text instead of through the structured channel. Two is enough to nudge a
    // recoverable model and gives up before looping forever on a model that just can't
    // do native function-calling at all.
    private const int MaxBadFormatRetries = 2;

    private const string AutoContinueFeedbackMessage =
        "Your previous response was cut off before completing (hit the output token limit " +
        "or the stream ended without a stop token). Continue from exactly where you left off; " +
        "do NOT restart, summarise, or apologise — just resume the next token.";

    private const string BadFormatFeedbackMessage =
        "Your previous response contained a tool call written as plain text " +
        "(e.g. <tool_call>…</tool_call>, <function=…> or similar XML). The runtime CANNOT " +
        "invoke tools written that way — those tags are treated as literal output. To " +
        "actually call a tool, emit the call through the structured function-calling " +
        "protocol your runtime exposes (the same way other tools were available to you " +
        "this turn). If you don't need a tool, just answer the user in plain prose.";

    private readonly IServiceProvider _services;
    private readonly ILogger<InteractiveRunner> _log;
    private readonly List<string> _history = new();
    private readonly ChatBuffer _chat = new();

    // ───── shared between background key reader and foreground turn loop ─────
    // Render lock serialises calls to ctx.Refresh() so the two threads don't
    // collide while mutating the Spectre layout. Lightweight — Render is cheap.
    private readonly SemaphoreSlim _renderLock = new(1, 1);
    // While a turn is being streamed, _activeStreamCts is non-null and Ctrl+C
    // from the key reader cancels via _activeStreamCts.Cancel(). When null, no
    // turn is in flight and Ctrl+C is a no-op (host Ctrl+C handler exits app).
    private CancellationTokenSource? _activeStreamCts;
    private volatile bool _isStreaming;
    private int _pendingQueueCount;       // Interlocked-incremented by the key reader, decremented by the foreground turn loop

    // Set by the key reader while more keystrokes are queued in the console input
    // buffer (i.e. mid-paste). Render() returns early; the LAST key in the batch
    // re-renders once with the final state. Turns a 1000-char paste's 1000 renders
    // into 1.
    private volatile bool _suppressRender;

    // Tracks whether the user has submitted the first non-slash line of this session.
    // While false (and the state has no turns), F8 ("Resume last in this dir") is
    // offered in the header; once a real turn starts, F8 is hidden. /new resets it.
    private volatile bool _firstTurnStarted;

    // Chat scrollback offset in WRAPPED chunks (not source lines). 0 = follow the
    // bottom; positive = scrolled up by N chunks. We bump this when new chunks
    // arrive so the visible window stays "locked" at the user's chosen position
    // rather than scrolling with content; reset to 0 when the user pages back down.
    private int _chatScrollOffset;
    private int _lastTotalChunks;

    // Confirmation modal state. Set when Ctrl+C is pressed while idle (with no
    // selection); held active until the user presses Ctrl+C a second time (after
    // the cooldown has elapsed) or dismisses with Esc.
    private volatile bool _exitConfirmActive;
    private DateTime _exitConfirmCooldownEndsUtc;
    private const int ExitConfirmCooldownMs = 1000;

    private static readonly (string Name, string Description)[] SlashCommands = new[]
    {
        ("/new",            "Start a fresh conversation (clears history)."),
        ("/resume <jobId>", "Load a previous job's state by id (or press F3 to pick from a list)."),
        ("/jobs",           "List the most recent 20 jobs."),
        ("/compress",       "Force a context compression pass right now."),
        ("/help",           "Show this list (or press F2)."),
        ("/exit",           "Quit DaggerAgent."),
    };

    public InteractiveRunner(IServiceProvider services, ILogger<InteractiveRunner> log)
    {
        _services = services;
        _log = log;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var agent = _services.GetRequiredService<LlmAgent>();
        var store = _services.GetRequiredService<IJobStore>();
        var openAi = _services.GetRequiredService<IOptions<OpenAIOptions>>().Value;
        var agentOpts = _services.GetRequiredService<IOptions<AgentOptions>>().Value;
        var mcpHost = _services.GetRequiredService<McpClientHost>();
        var builtIns = _services.GetRequiredService<BuiltInToolRegistry>();
        var memory = _services.GetRequiredService<MemoryStore>();

        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await memory.InitializeAsync(cancellationToken).ConfigureAwait(false);

        ConsoleIcon.TrySet(_log);

        var state = agent.CreateState(openAi.DefaultModel);
        var jobLogScope = _log.BeginScope(new Dictionary<string, object?> { ["JobId"] = state.Id });
        _log.LogInformation("Interactive runner started (jobId={JobId}, model={Model}, endpoint={Endpoint})",
            state.Id, state.Model, openAi.BaseUrl);
        using var ctRegistration = cancellationToken.Register(() =>
            _log.LogWarning("Interactive runner: host cancellationToken signalled while loop active"));

        UpdateTitle(state.Model);

        // Stdin-redirected mode (pipe / heredoc): no TUI, just read lines, run turns,
        // print final assistant text to stdout. Useful for one-shot scripts that feed
        // commands without wanting the full bordered UI.
        if (Console.IsInputRedirected)
        {
            return await RunRedirectedAsync(agent, state, cancellationToken).ConfigureAwait(false);
        }

        var input = new InputState();
        // Spectre's canonical TUI shape: Layout is the top-level Live renderable,
        // regions hold Panels. A Panel-wraps-Layout shape (what we had) makes the
        // Panel render at (layout-height + 2), overflowing Live's allotted height
        // by exactly 2 rows — top crops. Two stacked Panels share their adjacent
        // borders visually and give us "framed app" without the height clash.
        var input2 = 3;            // input panel: top-border + content + bottom-border
        var layout = new Layout("root")
            .SplitRows(
                new Layout("chat").Ratio(1),
                new Layout("input").Size(input2));

        var loopExitReason = "unset";
        // Take ownership of Ctrl+C: with this flag, Ctrl+C arrives via Console.ReadKey
        // as a normal key (KeyChar=0x03, Key=C, Modifiers=Control) instead of raising
        // Console.CancelKeyPress + SIGINT — so the Program.cs handler doesn't fire and
        // ProcessKeyAsync can decide whether to cancel a turn or exit the app.
        var prevTreatCtrlCAsInput = false;
        try { prevTreatCtrlCAsInput = Console.TreatControlCAsInput; Console.TreatControlCAsInput = true; } catch { }
        try
        {
            try { Console.SetWindowPosition(0, 0); } catch { /* not a TTY / not supported */ }
            Console.Write("\x1b[2J\x1b[H");
            try { Console.SetCursorPosition(0, 0); } catch { /* not a TTY / not supported */ }
            _log.LogDebug("TUI pre-Live: cursor=({CX},{CY}) window=({WL},{WT}) buf=({BW},{BH}) win=({WW},{WH})",
                SafeGetCursorLeft(), SafeGetCursorTop(),
                SafeGetWindowLeft(), SafeGetWindowTop(),
                SafeGetBufferWidth(), SafeGetBufferHeight(),
                SafeGetWindowWidth(), SafeGetWindowHeight());

            await AnsiConsole.Live(layout)
                .AutoClear(true)
                .Overflow(VerticalOverflow.Crop)
                .Cropping(VerticalOverflowCropping.Top)
                .StartAsync(async ctx =>
                {
                    // Lines submitted via Enter go onto this channel. The background key
                    // reader writes; the foreground turn loop reads. This is what lets the
                    // user type and queue another message while a turn is still streaming.
                    var lineChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
                    {
                        SingleReader = true,
                        SingleWriter = true,
                    });

                    // Background key reader. Owns ReadKey/edit/Enter/Ctrl+C handling.
                    // Keeps running through the whole session so input is always live —
                    // including during a streaming agent turn.
                    var keyReaderTask = ReadKeysLoopAsync(ctx, layout, state, openAi, mcpHost, builtIns,
                        input, store, lineChannel.Writer, cancellationToken);

                    Render(ctx, layout, state, openAi, mcpHost, builtIns, input);

                    try
                    {
                        // Foreground turn loop: pull next user line, run it, repeat. Slow
                        // turns don't block input — the key reader keeps responding.
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            string line;
                            try
                            {
                                line = await lineChannel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                loopExitReason = "host token cancellation while awaiting next queued line";
                                break;
                            }
                            Interlocked.Decrement(ref _pendingQueueCount);

                            // User-prompt echo: dim grey so it's visually distinct from
                            // agent output (which renders in default/bright colours).
                            _chat.AddLine($"[grey]>[/] [grey]{Markup.Escape(line)}[/]");
                            Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
                            _log.LogInformation("Chat in (jobId={JobId} turn={Turn}): {Input}", state.Id, state.TurnsTaken, line);

                            if (line.StartsWith("/"))
                            {
                                var keepRunning = await HandleSlashCommandAsync(line, state, store, agent, openAi, ctx, layout, mcpHost, builtIns, input, cancellationToken).ConfigureAwait(false);
                                if (!keepRunning) { loopExitReason = $"slash command exit: {line}"; break; }
                                continue;
                            }

                            using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            _activeStreamCts = streamCts;
                            _isStreaming = true;
                            _firstTurnStarted = true;
                            Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
                            try
                            {
                                await StreamAgentTurnAsync(agent, state, line, agentOpts, ctx, layout, openAi, mcpHost, builtIns, input, streamCts.Token).ConfigureAwait(false);

                                // Some small / mis-configured models emit tool calls as plain text
                                // (e.g. "<tool_call><function=foo>…</function></tool_call>") instead
                                // of through the structured tool_calls channel — our runtime can't
                                // invoke those, so the agent silently appears to have done nothing.
                                // Detect the pattern in the final assistant message and re-run the
                                // turn with a corrective user message asking it to use the proper
                                // protocol. Capped at MaxBadFormatRetries to avoid infinite loops.
                                for (var retry = 1; retry <= MaxBadFormatRetries && !streamCts.IsCancellationRequested; retry++)
                                {
                                    if (!LastAssistantHasTextFormattedToolCall(state)) break;
                                    _log.LogWarning("Detected text-formatted tool call in assistant reply — retry {Retry}/{Max}", retry, MaxBadFormatRetries);
                                    _chat.AddLine($"[yellow][[detected text-format tool call — asking the model to retry through the proper tool-call protocol ({retry}/{MaxBadFormatRetries})]][/]");
                                    Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
                                    await StreamAgentTurnAsync(agent, state, BadFormatFeedbackMessage,
                                        agentOpts, ctx, layout, openAi, mcpHost, builtIns, input, streamCts.Token).ConfigureAwait(false);
                                }

                                // Auto-continue when the LLM stopped mid-thought rather than on a
                                // real EOS. "length" = hit max_tokens. Null/empty = no finish_reason
                                // from server, typically a stream cut short. "tool_calls" is handled
                                // by MEAI internally; if we see it here as the FINAL finish reason
                                // something's odd but a continuation won't fix it.
                                for (var cont = 1;
                                     cont <= agentOpts.MaxAutoContinues
                                     && agentOpts.AutoContinueOnIncomplete
                                     && !streamCts.IsCancellationRequested;
                                     cont++)
                                {
                                    var fr = state.LastTurnFinishReason;
                                    if (string.Equals(fr, "stop", StringComparison.OrdinalIgnoreCase)) break;
                                    if (string.Equals(fr, "tool_calls", StringComparison.OrdinalIgnoreCase)) break;
                                    if (string.Equals(fr, "content_filter", StringComparison.OrdinalIgnoreCase))
                                    {
                                        _chat.AddLine("[yellow][[response stopped by content filter — not auto-continuing]][/]");
                                        Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
                                        break;
                                    }
                                    var reasonLabel = string.IsNullOrEmpty(fr) ? "stream cut short (no finish_reason)" : $"finish_reason={fr}";
                                    _log.LogInformation("Auto-continue: {Reason} on job {JobId} ({Retry}/{Max})", reasonLabel, state.Id, cont, agentOpts.MaxAutoContinues);
                                    _chat.AddLine($"[yellow][[response incomplete ({Markup.Escape(reasonLabel)}) — auto-continuing ({cont}/{agentOpts.MaxAutoContinues})]][/]");
                                    Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
                                    await StreamAgentTurnAsync(agent, state, AutoContinueFeedbackMessage,
                                        agentOpts, ctx, layout, openAi, mcpHost, builtIns, input, streamCts.Token).ConfigureAwait(false);
                                }
                            }
                            catch (OperationCanceledException) when (streamCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                            {
                                // User pressed Ctrl+C — distinct from the SDK NetworkTimeout case.
                                _chat.AddLine("[yellow][[turn cancelled by Ctrl+C]][/]");
                                _log.LogInformation("Interactive turn cancelled by user (Ctrl+C)");
                            }
                            catch (OperationCanceledException oce) when (!cancellationToken.IsCancellationRequested)
                            {
                                _log.LogWarning(oce, "Interactive turn timed out / cancelled internally — continuing");
                                _chat.AddLine("[yellow][[turn cancelled — likely OpenAI:RequestTimeoutSeconds]][/]");
                            }
                            catch (OperationCanceledException oce)
                            {
                                loopExitReason = $"OperationCanceledException with host token cancelled ({oce.Message})";
                                _log.LogWarning(oce, "Interactive turn cancelled by host");
                                break;
                            }
                            catch (Exception ex)
                            {
                                _log.LogError(ex, "Interactive turn failed");
                                _chat.AddLine($"[red][[error]][/] {Markup.Escape(ex.Message)}");
                            }
                            finally
                            {
                                _isStreaming = false;
                                _activeStreamCts = null;
                                Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
                            }
                        }
                    }
                    finally
                    {
                        // Signal the key reader to stop and wait briefly for it to drain.
                        lineChannel.Writer.TryComplete();
                        try { await keyReaderTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                        catch { /* best-effort */ }
                    }
                }).ConfigureAwait(false);

            if (loopExitReason == "unset")
                loopExitReason = cancellationToken.IsCancellationRequested
                    ? "host cancellationToken signalled"
                    : "Live loop ended without explicit reason";

            return 0;
        }
        finally
        {
            try { Console.TreatControlCAsInput = prevTreatCtrlCAsInput; } catch { }
            _log.LogInformation("Interactive runner exiting: {Reason}", loopExitReason);
            jobLogScope?.Dispose();
        }
    }

    // ──────────────────────────── render helpers ────────────────────────────

    private static Panel WrapChat(IRenderable content, bool showResumeLast)
    {
        var header = " [bold red]† DaggerAgent[/]  [grey]·[/]  [grey]F1[/] [dim]Info[/]  [grey]·[/]  [grey]F2[/] [dim]Commands[/]  [grey]·[/]  [grey]F3[/] [dim]Sessions[/]";
        if (showResumeLast)
            header += "  [grey]·[/]  [grey]F8[/] [dim]Resume[/]";
        header += " ";
        return new Panel(content)
        {
            Border = BoxBorder.Rounded,
            Expand = true,
            Padding = new Padding(1, 0, 1, 0),
            Header = new PanelHeader(header) { Justification = Justify.Left },
            BorderStyle = new Style(foreground: Color.Grey),
        };
    }

    private Panel WrapInput(IRenderable content, bool hasSelection)
    {
        var panel = new Panel(content)
        {
            Border = BoxBorder.Rounded,
            Expand = true,
            Padding = new Padding(1, 0, 1, 0),
            BorderStyle = new Style(foreground: Color.Grey),
        };
        // Header reflects what Ctrl+C does right now:
        //   selection present → "Ctrl+C copy"
        //   streaming         → "Ctrl+C cancel"
        //   idle              → "Ctrl+C exit"
        // Plus "(+N queued)" when the user has typed ahead during a turn.
        var ctrlCHint =
            hasSelection ? "[grey]Ctrl+C[/] [dim]copy[/]"
            : _isStreaming ? "[grey]Ctrl+C[/] [dim]cancel[/]"
            : "[grey]Ctrl+C[/] [dim]exit[/]";
        var parts = new List<string> { ctrlCHint };
        var q = Volatile.Read(ref _pendingQueueCount);
        if (q > 0) parts.Add($"[dim](+{q} queued)[/]");
        panel.Header = new PanelHeader(" " + string.Join("  [grey]·[/]  ", parts) + " ")
        {
            Justification = Justify.Left,
        };
        return panel;
    }

    private int _lastReportedHeight = -1;

    private void Render(LiveDisplayContext ctx, Layout layout, ConversationState state,
        OpenAIOptions openAi, McpClientHost mcpHost, BuiltInToolRegistry builtIns, InputState input)
    {
        // Bail out cheaply during a paste batch — the LAST key in the batch will run
        // through the full render with the final state.
        if (_suppressRender) return;
        // Lock is held inside Render so the background key reader and the foreground
        // streaming/turn loop can't both mutate the Layout regions or call Refresh()
        // concurrently. Render is fast (single-digit ms even with wrap), so a Wait()
        // here doesn't cost a noticeable amount.
        _renderLock.Wait();
        try
        {
            var consoleH = Console.WindowHeight;
            var profileH = AnsiConsole.Console.Profile.Height;
            var h = Math.Min(consoleH, profileH);

            // Input region grows with the buffer's line count, capped so chat doesn't
            // disappear entirely. Total panel rows = visibleLines + 2 borders.
            var maxInputContent = Math.Max(1, Math.Min(10, h - 8));
            var (inputRenderable, visibleInputLines) = BuildInput(input, maxInputContent);
            var inputSize = visibleInputLines + 2;

            layout["input"].Size(inputSize);

            var chatHeight = Math.Max(1, h - inputSize - /*chat borders*/2 - /*reserve*/1);

            if (h != _lastReportedHeight)
            {
                _lastReportedHeight = h;
                _log.LogDebug("TUI render dims: consoleHeight={ConsoleH}, profileHeight={ProfileH}, used={H}, chatHeight={ChatH}",
                    consoleH, profileH, h, chatHeight);
            }

            var showF8 = !_firstTurnStarted && state.TurnsTaken == 0;
            if (_exitConfirmActive)
                layout["chat"].Update(BuildExitModal());
            else
                layout["chat"].Update(WrapChat(BuildChat(chatHeight), showF8));
            layout["input"].Update(WrapInput(inputRenderable, input.HasSelection));
            ctx.Refresh();
        }
        finally { _renderLock.Release(); }
    }

    /// <summary>
    /// Centered red confirmation panel rendered into the chat region while
    /// <see cref="_exitConfirmActive"/> is set. Counts down a 1-second cooldown so a
    /// fat-fingered Ctrl+C can't immediately close the app.
    /// </summary>
    private IRenderable BuildExitModal()
    {
        var remaining = _exitConfirmCooldownEndsUtc - DateTime.UtcNow;
        var ready = remaining <= TimeSpan.Zero;

        var grid = new Grid().AddColumn();
        grid.AddEmptyRow();
        grid.AddRow(new Markup("[bold red]Press Ctrl+C again to close DaggerAgent[/]"));
        grid.AddEmptyRow();
        grid.AddRow(ready
            ? new Markup("[dim]Ready  ·  Esc to cancel[/]")
            : new Markup($"[dim]Ready in {Math.Ceiling(remaining.TotalMilliseconds / 100.0) / 10.0:0.0}s  ·  Esc to cancel[/]"));
        grid.AddEmptyRow();

        var panel = new Panel(grid)
        {
            Border = BoxBorder.Double,
            BorderStyle = new Style(foreground: Color.Red, decoration: Decoration.Bold),
            Padding = new Padding(2, 1, 2, 1),
            Header = new PanelHeader(" [bold red]Confirm exit[/] ") { Justification = Justify.Center },
        };
        return Align.Center(panel, VerticalAlignment.Middle);
    }

    /// <summary>
    /// Push the model / endpoint / tools / job / turn block into the chat region —
    /// triggered by F1. Cheap to recompute from current state on every press.
    /// </summary>
    private async Task ShowInfoInChatAsync(ConversationState state, OpenAIOptions openAi, McpClientHost mcpHost, BuiltInToolRegistry builtIns, CancellationToken ct)
    {
        var builtInCount = builtIns.ForAgent(state.Id, state.Depth).Count;
        var mcpCount = mcpHost.AllTools.Count;
        var mcpServers = mcpHost.ConnectionStatuses;
        var livePings = await PingConnectedMcpServersAsync(mcpHost, mcpServers, ct).ConfigureAwait(false);
        _chat.AddLine("[grey]── info ──[/]");
        _chat.AddLine($"  [grey]provider[/]  {Markup.Escape(openAi.Provider)}");
        _chat.AddLine($"  [grey]model[/]     [bold]{Markup.Escape(state.Model)}[/]");
        _chat.AddLine($"  [grey]endpoint[/]  [dim]{Markup.Escape(openAi.BaseUrl)}[/]");
        _chat.AddLine($"  [grey]tools[/]     {builtInCount} built-in + {mcpCount} mcp = {builtInCount + mcpCount} total");
        if (mcpServers.Count == 0)
        {
            _chat.AddLine("  [grey]mcp[/]       no servers configured");
        }
        else
        {
            _chat.AddLine("  [grey]mcp[/]       servers");
            foreach (var server in mcpServers)
            {
                var status = server.Status;
                if (string.Equals(status, "connected", StringComparison.OrdinalIgnoreCase) &&
                    livePings.TryGetValue(server.Name, out var live) &&
                    !live)
                {
                    status = "unreachable";
                }

                var color = McpStatusColor(status);
                var tools = server.ToolCount == 1 ? "1 tool" : $"{server.ToolCount} tools";
                var detail = string.IsNullOrWhiteSpace(server.Detail) ? "" : $" [dim]({Markup.Escape(server.Detail)})[/]";
                _chat.AddLine($"    - [bold]{Markup.Escape(server.Name)}[/] [{color}]{Markup.Escape(status)}[/] ({tools}) [dim]{Markup.Escape(server.Transport)}[/]{detail}");
            }
        }
        _chat.AddLine($"  [grey]job[/]       [dim]{Markup.Escape(state.Id)}[/]");
        _chat.AddLine($"  [grey]turn[/]      {state.TurnsTaken}");
        if (state.LastTurnTotalTokens > 0)
            _chat.AddLine($"  [grey]last turn[/] {state.LastTurnTotalTokens} tk, {state.LastTurnElapsedMs} ms");
        _chat.AddLine($"  [grey]totals[/]    in={state.TotalInputTokens} out={state.TotalOutputTokens} cost=${state.TotalCostUsd:F4}");
    }

    private static async Task<Dictionary<string, bool>> PingConnectedMcpServersAsync(
        McpClientHost mcpHost,
        IReadOnlyList<McpServerConnectionInfo> servers,
        CancellationToken ct)
    {
        var connected = servers
            .Where(s => string.Equals(s.Status, "connected", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (connected.Count == 0)
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        var results = await Task.WhenAll(connected.Select(async server =>
        {
            var ok = await mcpHost.PingAsync(server.Name, TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false);
            return (server.Name, ok);
        })).ConfigureAwait(false);

        return results.ToDictionary(r => r.Name, r => r.ok, StringComparer.OrdinalIgnoreCase);
    }

    private static string McpStatusColor(string status) =>
        status.ToLowerInvariant() switch
        {
            "connected" => "green",
            "unreachable" => "yellow",
            "skipped" => "yellow",
            "failed" => "red",
            "disabled" => "grey",
            _ => "grey",
        };

    private IRenderable BuildChat(int height)
    {
        // Inner panel width minus 1 column reserved for the scrollbar on the right.
        var width = Math.Max(8, Console.WindowWidth - 4 - 1);

        // Wrap EVERY source line so we know the full chunk count (needed for scrollbar
        // thumb sizing and the scroll-up clamp).
        var sourceLines = _chat.AllLines();
        var allChunks = new List<string>(sourceLines.Count + 16);
        foreach (var line in sourceLines)
            allChunks.AddRange(WrapMarkup(line, width));
        var totalChunks = allChunks.Count;

        // Lock-on-scroll: when the user is scrolled up and new chunks land at the
        // bottom, bump the offset by the same amount so the same content stays in
        // view. At the bottom (offset=0), do nothing → auto-follow new content.
        if (_chatScrollOffset > 0 && totalChunks > _lastTotalChunks)
            _chatScrollOffset += totalChunks - _lastTotalChunks;
        _lastTotalChunks = totalChunks;

        var maxOffset = Math.Max(0, totalChunks - height);
        if (_chatScrollOffset > maxOffset) _chatScrollOffset = maxOffset;
        if (_chatScrollOffset < 0) _chatScrollOffset = 0;

        var endIdx = totalChunks - _chatScrollOffset;
        var startIdx = Math.Max(0, endIdx - height);
        var visibleChunks = startIdx < endIdx ? allChunks.GetRange(startIdx, endIdx - startIdx) : new List<string>();
        var topBlanks = Math.Max(0, height - visibleChunks.Count);

        // Compose the content column as ONE Markup with N lines separated by '\n' —
        // Spectre.Console.Layout's Size(1) column then pins the scrollbar to a true
        // fixed 1-char width on the right; Grid was auto-sizing column 0 to the widest
        // line so the scrollbar drifted horizontally as content changed.
        var contentSb = new StringBuilder();
        for (var i = 0; i < height; i++)
        {
            if (i > 0) contentSb.Append('\n');
            if (i >= topBlanks) contentSb.Append(visibleChunks[i - topBlanks]);
        }
        IRenderable contentMarkup;
        try { contentMarkup = new Markup(contentSb.ToString()) { Overflow = Overflow.Ellipsis }; }
        catch (InvalidOperationException ex)
        {
            _log.LogError(ex, "BuildChat: Markup parse failed; falling back to escaped text");
            contentMarkup = new Markup(Markup.Escape(contentSb.ToString())) { Overflow = Overflow.Ellipsis };
        }

        // Scrollbar geometry. Thumb size proportional to visible/total; thumb position
        // reflects the scroll offset (bottom = thumb at the bottom of the track).
        // When everything fits in one screen we still reserve the column and render
        // spaces (no flicker between layouts as content grows past the screen edge).
        var sbSb = new StringBuilder();
        if (totalChunks <= height || height < 2)
        {
            for (var i = 0; i < height; i++)
            {
                if (i > 0) sbSb.Append('\n');
                sbSb.Append(' ');
            }
        }
        else
        {
            var thumbHeight = Math.Max(1, (int)Math.Round(height * (double)height / totalChunks));
            if (thumbHeight > height) thumbHeight = height;
            var fromBottom = maxOffset == 0 ? 0.0 : _chatScrollOffset / (double)maxOffset;
            var thumbTopFromBottom = (int)Math.Round((height - thumbHeight) * fromBottom);
            var thumbTop = (height - thumbHeight) - thumbTopFromBottom;
            for (var i = 0; i < height; i++)
            {
                if (i > 0) sbSb.Append('\n');
                if (i >= thumbTop && i < thumbTop + thumbHeight) sbSb.Append("[red]█[/]");
                else sbSb.Append("[grey39]│[/]");
            }
        }
        var scrollbarMarkup = new Markup(sbSb.ToString()) { Overflow = Overflow.Ellipsis };

        // Inner Layout: content (Ratio 1, fills remaining width) + scrollbar (Size 1).
        // Spectre's Size/Ratio splitter gives us an actual fixed-width right column.
        var inner = new Layout("chatInner")
            .SplitColumns(
                new Layout("chatContent").Ratio(1),
                new Layout("chatScrollbar").Size(1));
        inner["chatContent"].Update(contentMarkup);
        inner["chatScrollbar"].Update(scrollbarMarkup);
        return inner;
    }

    /// <summary>
    /// Split a Spectre markup line into chunks no wider than <paramref name="width"/>
    /// visible columns. Markup tags ([red]…[/], [[, ]]) are tracked so chunks have
    /// balanced styles — open tags are closed at the wrap boundary and reopened on
    /// the next chunk. Long single words still get hard-broken (this is a terminal,
    /// not a word processor). Returns at least one chunk.
    /// </summary>
    private static List<string> WrapMarkup(string text, int width)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) { result.Add(""); return result; }
        if (width <= 0) { result.Add(text); return result; }

        var sb = new StringBuilder();
        var openTags = new Stack<string>(); // outermost at bottom
        var visible = 0;

        void Break()
        {
            // Close open tags before emitting the chunk so the markup balances within
            // this line; reopen them on the next line so styling continues.
            var arr = openTags.ToArray(); // top-of-stack first
            for (var k = 0; k < arr.Length; k++) sb.Append("[/]");
            result.Add(sb.ToString());
            sb.Clear();
            for (var k = arr.Length - 1; k >= 0; k--) sb.Append('[').Append(arr[k]).Append(']');
            visible = 0;
        }

        var i = 0;
        while (i < text.Length)
        {
            // Literal '[[' / ']]'
            if (i + 1 < text.Length && text[i] == '[' && text[i + 1] == '[')
            {
                if (visible >= width) Break();
                sb.Append("[[");
                visible++;
                i += 2;
                continue;
            }
            if (i + 1 < text.Length && text[i] == ']' && text[i + 1] == ']')
            {
                if (visible >= width) Break();
                sb.Append("]]");
                visible++;
                i += 2;
                continue;
            }
            // Markup tag
            if (text[i] == '[')
            {
                var end = text.IndexOf(']', i + 1);
                if (end < 0)
                {
                    // Stray '['; treat as literal char and let it render as-is.
                    if (visible >= width) Break();
                    sb.Append(text[i]);
                    visible++;
                    i++;
                    continue;
                }
                var content = text.Substring(i + 1, end - i - 1);
                sb.Append('[').Append(content).Append(']');
                if (content == "/")
                {
                    if (openTags.Count > 0) openTags.Pop();
                }
                else
                {
                    openTags.Push(content);
                }
                i = end + 1;
                continue;
            }
            // Explicit newline — break here.
            if (text[i] == '\n')
            {
                Break();
                i++;
                continue;
            }
            // Regular visible char.
            if (visible >= width) Break();
            sb.Append(text[i]);
            visible++;
            i++;
        }
        result.Add(sb.ToString());
        return result;
    }

    /// <summary>
    /// Render the input buffer as 1..N visible lines. Each buffer line maps to one
    /// rendered row; the cursor's row gets horizontal scrolling so the cursor stays
    /// visible. Selection is rendered with inverse style and respects line boundaries.
    /// Returns (renderable, visibleRowCount) so Render can size the input region.
    /// </summary>
    private static (IRenderable Renderable, int VisibleLines) BuildInput(InputState input, int maxLines)
    {
        var totalWidth = Console.WindowWidth;
        var available = Math.Max(4, totalWidth - 2 - 2 - 2 - 2);

        var buffer = input.Buffer.ToString();
        var lines = buffer.Split('\n');
        var (cursorRow, cursorCol) = input.CursorRowCol;

        // Scroll vertically so the cursor row sits inside the visible window.
        var visibleCount = Math.Min(lines.Length, Math.Max(1, maxLines));
        var firstVisibleRow = Math.Max(0, cursorRow - (visibleCount - 1));
        firstVisibleRow = Math.Min(firstVisibleRow, Math.Max(0, lines.Length - visibleCount));

        var (selStart, selEnd) = input.SelectionRange;

        var sb = new StringBuilder();
        for (var i = 0; i < visibleCount; i++)
        {
            var row = firstVisibleRow + i;
            if (i > 0) sb.Append('\n');

            // Line prefix: first visible row gets the "› " prompt, continuation rows
            // get an indent so the cursor column aligns with the first row's text.
            var prefix = row == 0 ? "[bold red]›[/] " : "  ";
            sb.Append(prefix);

            var line = lines[row];

            if (row == cursorRow)
            {
                // Active row — apply horizontal scroll so the cursor stays visible.
                if (cursorCol < input.ScrollOffset) input.ScrollOffset = cursorCol;
                else if (cursorCol > input.ScrollOffset + available - 1)
                    input.ScrollOffset = cursorCol - available + 1;
                if (input.ScrollOffset < 0) input.ScrollOffset = 0;
                if (input.ScrollOffset > Math.Max(0, line.Length))
                    input.ScrollOffset = Math.Max(0, line.Length);

                var visibleLen = Math.Max(0, Math.Min(available, line.Length - input.ScrollOffset));
                var visible = visibleLen > 0 ? line.Substring(input.ScrollOffset, visibleLen) : "";
                var localCursor = cursorCol - input.ScrollOffset;

                var leftInd = input.ScrollOffset > 0 ? "[grey]‹[/]" : " ";
                var rightInd = input.ScrollOffset + visibleLen < line.Length ? "[grey]›[/]" : " ";

                AppendLineWithCursorOrSelection(sb, visible, localCursor,
                    leftInd, rightInd,
                    selStart, selEnd,
                    rowAbsStart: BufferAbsoluteOffsetForRow(buffer, row) + input.ScrollOffset,
                    rowVisibleAbsEnd: BufferAbsoluteOffsetForRow(buffer, row) + input.ScrollOffset + visibleLen,
                    isCursorRow: true,
                    hasSelection: input.HasSelection);
            }
            else
            {
                // Non-cursor row: truncate without scrolling; only selection styling needed.
                var visible = line.Length > available ? line.Substring(0, available) : line;
                AppendLineWithCursorOrSelection(sb, visible, localCursor: -1,
                    leftInd: " ", rightInd: " ",
                    selStart, selEnd,
                    rowAbsStart: BufferAbsoluteOffsetForRow(buffer, row),
                    rowVisibleAbsEnd: BufferAbsoluteOffsetForRow(buffer, row) + visible.Length,
                    isCursorRow: false,
                    hasSelection: input.HasSelection);
            }
        }

        return (new Markup(sb.ToString()) { Overflow = Overflow.Ellipsis }, visibleCount);
    }

    private static int BufferAbsoluteOffsetForRow(string buffer, int row)
    {
        if (row == 0) return 0;
        int current = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] == '\n')
            {
                current++;
                if (current == row) return i + 1;
            }
        }
        return buffer.Length;
    }

    /// <summary>
    /// Append one rendered line into <paramref name="sb"/>, applying inverse styling to
    /// any portion overlapping the selection range, and a cursor block on the active
    /// row when no selection is active.
    /// </summary>
    private static void AppendLineWithCursorOrSelection(StringBuilder sb,
        string visible, int localCursor,
        string leftInd, string rightInd,
        int selStart, int selEnd,
        int rowAbsStart, int rowVisibleAbsEnd,
        bool isCursorRow, bool hasSelection)
    {
        sb.Append(leftInd);

        if (hasSelection)
        {
            // Intersect this row's visible absolute range with the selection range.
            var loStart = Math.Max(selStart, rowAbsStart) - rowAbsStart;
            var loEnd = Math.Min(selEnd, rowVisibleAbsEnd) - rowAbsStart;
            if (loEnd <= loStart)
            {
                sb.Append(Markup.Escape(visible));
            }
            else
            {
                loStart = Math.Max(0, Math.Min(loStart, visible.Length));
                loEnd = Math.Max(loStart, Math.Min(loEnd, visible.Length));
                var before = visible.Substring(0, loStart);
                var sel = visible.Substring(loStart, loEnd - loStart);
                var after = visible.Substring(loEnd);
                sb.Append(Markup.Escape(before))
                  .Append("[white on red]").Append(Markup.Escape(sel)).Append("[/]")
                  .Append(Markup.Escape(after));
            }
        }
        else if (isCursorRow)
        {
            var clamped = Math.Max(0, Math.Min(localCursor, visible.Length));
            var before = visible.Substring(0, clamped);
            var after = clamped < visible.Length ? visible.Substring(clamped) : "";
            sb.Append(Markup.Escape(before))
              .Append("[bold red]").Append(CursorGlyph).Append("[/]")
              .Append(Markup.Escape(after));
        }
        else
        {
            sb.Append(Markup.Escape(visible));
        }

        sb.Append(rightInd);
    }

    // ──────────────────────────── background key reader ────────────────────────────

    /// <summary>
    /// Loops for the whole interactive session, reading single keystrokes and applying
    /// them. When Enter is pressed the assembled line is written to <paramref name="lineWriter"/>
    /// — the foreground turn loop picks it up. Ctrl+C while a turn is streaming cancels
    /// that turn via <see cref="_activeStreamCts"/>; outside a turn it's a no-op (the
    /// app-level Ctrl+C handler in Program.cs handles graceful exit).
    /// </summary>
    private async Task ReadKeysLoopAsync(LiveDisplayContext ctx, Layout layout, ConversationState state,
        OpenAIOptions openAi, McpClientHost mcpHost, BuiltInToolRegistry builtIns, InputState input,
        IJobStore store, ChannelWriter<string> lineWriter, CancellationToken ct)
    {
        var lastW = Console.WindowWidth;
        var lastH = Console.WindowHeight;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                while (!Console.KeyAvailable)
                {
                    if (ct.IsCancellationRequested) return;
                    var w = Console.WindowWidth;
                    var h = Console.WindowHeight;
                    var resizeChanged = w != lastW || h != lastH;
                    if (resizeChanged) { lastW = w; lastH = h; }
                    // Re-render while the exit-confirmation modal is up so the cooldown
                    // countdown ticks visibly. ~25 fps is plenty smooth for a 1s timer
                    // and the render is cheap.
                    if (resizeChanged || _exitConfirmActive)
                        Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
                    // 20ms ≈ 50Hz key-availability polling. The worst-case keystroke-to-
                    // render latency is bounded by this interval; halving it from the
                    // previous 40ms shaves perceptible typing lag at negligible CPU cost.
                    await Task.Delay(20, ct).ConfigureAwait(false);
                }
                var key = Console.ReadKey(intercept: true);
                // If more keys are already queued (paste in progress), suppress the
                // per-key render — the last key in the batch will see KeyAvailable=false
                // here and re-enable rendering, so the final state shows up in one go.
                _suppressRender = Console.KeyAvailable;
                try
                {
                    await ProcessKeyAsync(key, ctx, layout, state, openAi, mcpHost, builtIns, input, store, lineWriter, ct).ConfigureAwait(false);
                }
                finally
                {
                    _suppressRender = false;
                }
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            _log.LogError(ex, "Background key reader crashed");
        }
    }

    private async Task ProcessKeyAsync(ConsoleKeyInfo key,
        LiveDisplayContext ctx, Layout layout, ConversationState state,
        OpenAIOptions openAi, McpClientHost mcpHost, BuiltInToolRegistry builtIns, InputState input,
        IJobStore store, ChannelWriter<string> lineWriter, CancellationToken ct)
    {
        var isCtrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
        var isShift = (key.Modifiers & ConsoleModifiers.Shift) != 0;

        // Ctrl+C is context-sensitive:
        //   selection present → copy selection to clipboard (don't cancel / don't exit)
        //   streaming         → cancel the in-flight turn
        //   idle              → request graceful app exit
        // Arrives here (instead of CancelKeyPress) because the interactive session sets
        // Console.TreatControlCAsInput=true.
        if (isCtrl && key.Key == ConsoleKey.C)
        {
            // Selection > streaming > idle, in priority order. Idle path goes through
            // a confirm-modal with a 1s cooldown so a stray Ctrl+C doesn't close the app.
            if (input.HasSelection)
            {
                var (start, end) = input.SelectionRange;
                var text = input.Buffer.ToString(start, end - start);
                if (TryCopyToClipboard(text))
                    _log.LogDebug("Copied {N} chars to clipboard", text.Length);
                else
                    _log.LogWarning("Clipboard copy unavailable on this platform");
                return;
            }
            if (_isStreaming && _activeStreamCts is not null && !_activeStreamCts.IsCancellationRequested)
            {
                _activeStreamCts.Cancel();
                return;
            }
            // Idle. Either prime the confirmation modal or, if it's already active and
            // past the cooldown, actually exit.
            if (!_exitConfirmActive)
            {
                _exitConfirmActive = true;
                _exitConfirmCooldownEndsUtc = DateTime.UtcNow.AddMilliseconds(ExitConfirmCooldownMs);
                Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
                return;
            }
            if (DateTime.UtcNow < _exitConfirmCooldownEndsUtc)
            {
                // Still inside the cooldown — ignore. The modal stays up.
                return;
            }
            var lifetime = _services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
            _log.LogInformation("Ctrl+C confirmed → requesting graceful app exit");
            lifetime.StopApplication();
            return;
        }

        // Any keystroke other than Ctrl+C / Esc is ignored while the modal is up so the
        // user can't accidentally edit the input behind the confirmation.
        if (_exitConfirmActive)
        {
            if (key.Key == ConsoleKey.Escape)
            {
                _exitConfirmActive = false;
                _log.LogDebug("Exit confirmation dismissed by Esc");
                Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
            }
            return;
        }

        if (isCtrl && key.Key == ConsoleKey.A)
        {
            if (input.Buffer.Length == 0) return;
            input.SelectionAnchor = 0;
            input.Cursor = input.Buffer.Length;
            Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
            return;
        }
        if (isCtrl && key.Key == ConsoleKey.L) { _chat.Clear(); Render(ctx, layout, state, openAi, mcpHost, builtIns, input); return; }
        if (isCtrl && key.Key == ConsoleKey.U) { input.Clear(); Render(ctx, layout, state, openAi, mcpHost, builtIns, input); return; }
        if (isCtrl && key.Key == ConsoleKey.W)
        {
            if (input.HasSelection) { input.DeleteSelection(); Render(ctx, layout, state, openAi, mcpHost, builtIns, input); return; }
            if (input.Cursor == 0) return;
            var start = PrevWordBoundary(input.Buffer, input.Cursor);
            input.Buffer.Remove(start, input.Cursor - start);
            input.Cursor = start;
            Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
            return;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            // Shift+Enter explicitly inserts a newline. Plain Enter normally submits,
            // BUT if there's already more input queued in the console buffer we're
            // almost certainly in the middle of a paste of multi-line text — treat
            // this Enter as a literal newline so the paste is preserved verbatim.
            // (A deliberate human Enter has ~50ms+ before the next keystroke; a paste
            // delivers the whole clipboard within a couple of ms, so KeyAvailable is
            // a reliable discriminator.)
            if (isShift || Console.KeyAvailable)
            {
                if (input.HasSelection) input.DeleteSelection();
                input.Buffer.Insert(input.Cursor, '\n');
                input.Cursor++;
                Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
                return;
            }
            var line = input.Buffer.ToString();
            input.Clear();
            if (!string.IsNullOrWhiteSpace(line))
            {
                RecordHistory(line);
                Interlocked.Increment(ref _pendingQueueCount);
                await lineWriter.WriteAsync(line, ct).ConfigureAwait(false);
            }
            Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
            return;
        }
        if (key.Key == ConsoleKey.Escape) { input.Clear(); Render(ctx, layout, state, openAi, mcpHost, builtIns, input); return; }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (input.HasSelection)
            {
                input.DeleteSelection();
                Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
            }
            else if (input.Cursor > 0)
            {
                input.Buffer.Remove(input.Cursor - 1, 1);
                input.Cursor--;
                Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
            }
            return;
        }
        if (key.Key == ConsoleKey.Delete)
        {
            if (input.HasSelection)
            {
                input.DeleteSelection();
                Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
            }
            else if (input.Cursor < input.Buffer.Length)
            {
                input.Buffer.Remove(input.Cursor, 1);
                Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
            }
            return;
        }

        if (key.Key == ConsoleKey.LeftArrow)
        {
            MoveCursor(input, isShift, target: isCtrl ? PrevWordBoundary(input.Buffer, input.Cursor) : Math.Max(0, input.Cursor - 1));
            Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
            return;
        }
        if (key.Key == ConsoleKey.RightArrow)
        {
            MoveCursor(input, isShift, target: isCtrl ? NextWordBoundary(input.Buffer, input.Cursor) : Math.Min(input.Buffer.Length, input.Cursor + 1));
            Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
            return;
        }
        if (key.Key == ConsoleKey.Home) { MoveCursor(input, isShift, target: 0); Render(ctx, layout, state, openAi, mcpHost, builtIns, input); return; }
        if (key.Key == ConsoleKey.End)  { MoveCursor(input, isShift, target: input.Buffer.Length); Render(ctx, layout, state, openAi, mcpHost, builtIns, input); return; }

        // Chat scrollback paging. The scroll offset is clamped in BuildChat so big jumps
        // are fine — they get clipped to the available scrollback range.
        if (key.Key == ConsoleKey.PageUp)
        {
            var step = Math.Max(3, Console.WindowHeight / 3);
            _chatScrollOffset += step;
            Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
            return;
        }
        if (key.Key == ConsoleKey.PageDown)
        {
            var step = Math.Max(3, Console.WindowHeight / 3);
            _chatScrollOffset = Math.Max(0, _chatScrollOffset - step);
            Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
            return;
        }

        if (key.Key == ConsoleKey.UpArrow)
        {
            // Ctrl+Up = history back. Plain Up moves cursor up a line (multi-line input);
            // in single-line input it's a no-op.
            if (isCtrl)
            {
                if (_history.Count == 0) return;
                var next = input.HistoryIdx == -1 ? _history.Count - 1 : Math.Max(0, input.HistoryIdx - 1);
                LoadFromHistory(input, next);
                Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
                return;
            }
            var (row, col) = input.CursorRowCol;
            if (row == 0) return; // already on first line
            MoveCursor(input, isShift, target: input.RowColToIndex(row - 1, col));
            Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
            return;
        }
        if (key.Key == ConsoleKey.DownArrow)
        {
            if (isCtrl)
            {
                if (input.HistoryIdx == -1) return;
                if (input.HistoryIdx >= _history.Count - 1) RestoreDraft(input);
                else LoadFromHistory(input, input.HistoryIdx + 1);
                Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
                return;
            }
            var (row, col) = input.CursorRowCol;
            if (row >= input.LineCount - 1) return; // already on last line
            MoveCursor(input, isShift, target: input.RowColToIndex(row + 1, col));
            Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
            return;
        }

        if (key.Key == ConsoleKey.F1)
        {
            await ShowInfoInChatAsync(state, openAi, mcpHost, builtIns, ct).ConfigureAwait(false);
            Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
            return;
        }
        if (key.Key == ConsoleKey.F2)
        {
            ShowSlashCommandMenu();
            Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
            return;
        }
        if (key.Key == ConsoleKey.F3)
        {
            var jobs = await store.ListAsync(20, ct).ConfigureAwait(false);
            ShowSessionPickerInChat(jobs);
            input.Buffer.Clear();
            input.Buffer.Append("/resume ");
            input.Cursor = input.Buffer.Length;
            Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
            return;
        }
        if (key.Key == ConsoleKey.F8)
        {
            // Only valid before the user submits the first real turn — once they have,
            // the visible state isn't "fresh" and silently swapping to a different job
            // would be surprising.
            if (_firstTurnStarted || state.TurnsTaken > 0) return;
            await ResumeLastInCwdAsync(state, store, ctx, layout, openAi, mcpHost, builtIns, input, ct).ConfigureAwait(false);
            return;
        }

        if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
        {
            // Typing over a selection replaces it (standard text-editor behaviour).
            if (input.HasSelection) input.DeleteSelection();
            input.Buffer.Insert(input.Cursor, key.KeyChar);
            input.Cursor++;
            Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
        }
    }

    /// <summary>
    /// Move the cursor, optionally extending an existing selection. With
    /// <paramref name="extend"/>=true, the anchor is established (if not already
    /// set) before the cursor moves, so the range between anchor and new cursor
    /// becomes the selection. With extend=false, any existing selection is
    /// dropped and the cursor moves to the new position.
    /// </summary>
    private static void MoveCursor(InputState input, bool extend, int target)
    {
        if (extend)
        {
            if (!input.SelectionAnchor.HasValue) input.SelectionAnchor = input.Cursor;
            input.Cursor = target;
            // Collapsed selection (anchor == cursor) is the same as no selection.
            if (input.SelectionAnchor.Value == input.Cursor) input.SelectionAnchor = null;
        }
        else
        {
            input.SelectionAnchor = null;
            input.Cursor = target;
        }
    }


    private void LoadFromHistory(InputState input, int idx)
    {
        if (input.HistoryIdx == -1) input.HistoryDraft = input.Buffer.ToString();
        input.HistoryIdx = idx;
        input.Buffer.Clear();
        input.Buffer.Append(_history[idx]);
        input.Cursor = input.Buffer.Length;
        input.SelectionAnchor = null;
    }

    private static void RestoreDraft(InputState input)
    {
        input.HistoryIdx = -1;
        input.Buffer.Clear();
        if (input.HistoryDraft is not null) input.Buffer.Append(input.HistoryDraft);
        input.Cursor = input.Buffer.Length;
        input.HistoryDraft = null;
        input.SelectionAnchor = null;
    }

    private static int PrevWordBoundary(StringBuilder sb, int from)
    {
        var i = from - 1;
        while (i >= 0 && char.IsWhiteSpace(sb[i])) i--;
        while (i >= 0 && !char.IsWhiteSpace(sb[i])) i--;
        return Math.Max(0, i + 1);
    }

    private static int NextWordBoundary(StringBuilder sb, int from)
    {
        var i = from;
        while (i < sb.Length && !char.IsWhiteSpace(sb[i])) i++;
        while (i < sb.Length && char.IsWhiteSpace(sb[i])) i++;
        return Math.Min(sb.Length, i);
    }

    /// <summary>
    /// True when the final assistant message in <paramref name="state"/>.History looks
    /// like it tried to call a tool by emitting raw XML/text (Qwen / Hermes / Llama
    /// "tool_call" dialects) instead of using the structured tool_calls channel.
    /// Conservative: requires both an opening and closing tag in the same message so
    /// a user discussing the format in prose doesn't trigger a false positive.
    /// </summary>
    private static bool LastAssistantHasTextFormattedToolCall(ConversationState state)
    {
        if (state.History.Count == 0) return false;
        var last = state.History[^1];
        if (last.Role != ChatRole.Assistant) return false;
        var text = last.Text ?? "";
        if (string.IsNullOrEmpty(text)) return false;
        return HasBalancedPair(text, "<tool_call>",     "</tool_call>")
            || HasBalancedPair(text, "<function_call>", "</function_call>");
    }

    private static bool HasBalancedPair(string text, string open, string close)
    {
        var o = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (o < 0) return false;
        var c = text.IndexOf(close, o + open.Length, StringComparison.OrdinalIgnoreCase);
        return c > o;
    }

    private void RecordHistory(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (_history.Count > 0 && _history[^1] == line) return;
        _history.Add(line);
        if (_history.Count > HistoryCap) _history.RemoveAt(0);
    }

    // ──────────────────────────── streaming a turn ────────────────────────────

    private async Task StreamAgentTurnAsync(LlmAgent agent, ConversationState state, string line,
        AgentOptions agentOpts, LiveDisplayContext ctx, Layout layout,
        OpenAIOptions openAi, McpClientHost mcpHost, BuiltInToolRegistry builtIns, InputState input,
        CancellationToken ct)
    {
        var showThinking = agentOpts.ShowThinking;
        var showToolCalls = agentOpts.ShowToolCalls;

        var pendingAnswer = new StringBuilder();
        var pendingThinking = new StringBuilder();
        var postToolPhase = false;
        var announcedCalls = new HashSet<string>(StringComparer.Ordinal);
        var splitter = new ThinkingSplitter();
        var transcript = new StringBuilder();
        var thinkingTranscript = new StringBuilder();

        // Each token chunk arrives as a streaming update. We accumulate the in-progress
        // answer in pendingAnswer, replace it on the trailing chat line on every
        // refresh, and keep going. Tool calls/results get their own permanent lines.
        _chat.BeginPending();

        void Flush()
        {
            var thinking = pendingThinking.ToString();
            var answer = pendingAnswer.ToString();
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(thinking) && showThinking)
            {
                sb.Append("[grey]").Append(Markup.Escape(thinking)).Append("[/]");
            }
            if (!string.IsNullOrEmpty(answer))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("[bold red]†[/] ").Append(Markup.Escape(answer));
            }
            if (sb.Length == 0) sb.Append("[dim]† …[/]");
            _chat.UpdatePending(sb.ToString());
            Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
        }

        await foreach (var update in agent.RunStreamingTurnAsync(state, line, ct).ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent rc when !string.IsNullOrEmpty(rc.Text):
                        if (postToolPhase)
                        {
                            foreach (var seg in splitter.Push(rc.Text))
                            {
                                if (seg.IsThinking) pendingThinking.Append(seg.Text);
                                else pendingAnswer.Append(seg.Text);
                                (seg.IsThinking ? thinkingTranscript : transcript).Append(seg.Text);
                            }
                        }
                        else
                        {
                            pendingThinking.Append(rc.Text);
                            thinkingTranscript.Append(rc.Text);
                        }
                        Flush();
                        break;
                    case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                        foreach (var seg in splitter.Push(tc.Text))
                        {
                            if (seg.IsThinking) pendingThinking.Append(seg.Text);
                            else pendingAnswer.Append(seg.Text);
                            (seg.IsThinking ? thinkingTranscript : transcript).Append(seg.Text);
                        }
                        Flush();
                        break;
                    case FunctionCallContent fc when showToolCalls:
                        postToolPhase = true;
                        if (!string.IsNullOrEmpty(fc.Name) && announcedCalls.Add(fc.CallId ?? Guid.NewGuid().ToString()))
                        {
                            // Promote any pending streaming output to a permanent line
                            // before announcing the tool call, so its order is preserved.
                            _chat.CommitPending();
                            pendingAnswer.Clear(); pendingThinking.Clear();
                            var args = ToolCallFormatter.FormatArgs(fc.Arguments);
                            _chat.AddLine($"[red]→ {Markup.Escape(fc.Name)}([dim]{Markup.Escape(args)}[/])[/]");
                            _chat.BeginPending();
                            _log.LogInformation("Tool call (jobId={JobId} turn={Turn}): {Name}({Args})",
                                state.Id, state.TurnsTaken, fc.Name, args);
                            Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
                        }
                        break;
                    case FunctionResultContent fr when showToolCalls:
                        postToolPhase = true;
                        var resultText = fr.Result?.ToString() ?? "";
                        var excerpt = ToolCallFormatter.SafeExcerpt(resultText, 120);
                        _chat.CommitPending();
                        pendingAnswer.Clear(); pendingThinking.Clear();
                        _chat.AddLine($"[red]← {Markup.Escape(excerpt)} [dim]({resultText.Length} chars)[/][/]");
                        _chat.BeginPending();
                        _log.LogInformation("Tool result (jobId={JobId} turn={Turn}): {Chars} chars — {Excerpt}",
                            state.Id, state.TurnsTaken, resultText.Length, ToolCallFormatter.SafeExcerpt(resultText, 500));
                        Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
                        break;
                    case FunctionCallContent:
                    case FunctionResultContent:
                        postToolPhase = true;
                        break;
                }
            }
        }
        // Flush trailing thinking remnant from splitter (e.g. unclosed <think>)
        var tail = splitter.Flush();
        if (tail is not null)
        {
            if (tail.Value.IsThinking) pendingThinking.Append(tail.Value.Text);
            else pendingAnswer.Append(tail.Value.Text);
            (tail.Value.IsThinking ? thinkingTranscript : transcript).Append(tail.Value.Text);
        }
        Flush();
        _chat.CommitPending();

        if (agentOpts.ShowTurnStats && state.LastTurnTotalTokens > 0)
        {
            var stats = TurnStatsFormatter.Format(
                state.LastTurnTotalTokens,
                state.LastTurnOutputTokens,
                state.LastTurnElapsedMs,
                state.LastTurnLlmElapsedMs,
                state.LastTurnToolCalls);
            _chat.AddLine($"[grey]{Markup.Escape(stats)}[/]");
        }
        _log.LogInformation("Chat out (jobId={JobId} turn={Turn}, {Chars} chars, {ThinkChars} thinking chars): {Reply}",
            state.Id, state.TurnsTaken, transcript.Length, thinkingTranscript.Length, transcript.ToString());
        Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
    }

    // ──────────────────────────── slash commands ────────────────────────────

    private async Task<bool> HandleSlashCommandAsync(string line, ConversationState state, IJobStore store,
        LlmAgent agent, OpenAIOptions openAi, LiveDisplayContext ctx, Layout layout,
        McpClientHost mcpHost, BuiltInToolRegistry builtIns, InputState input, CancellationToken ct)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        switch (cmd)
        {
            case "/exit":
            case "/quit":
                return false;
            case "/new":
                state.Id = Guid.NewGuid().ToString("N");
                state.History.Clear();
                state.History.Add(new ChatMessage(ChatRole.System, state.SystemPrompt));
                state.TurnsTaken = 0;
                state.ApproxTokenCount = 0;
                state.TotalInputTokens = 0;
                state.TotalOutputTokens = 0;
                state.TotalCostUsd = 0;
                state.Model = openAi.DefaultModel;
                state.CreatedAt = state.UpdatedAt = DateTimeOffset.UtcNow;
                state.WorkingDirectory = _services.GetRequiredService<Configuration.HostLaunchInfo>().OriginalWorkingDirectory;
                _firstTurnStarted = false; // fresh session: re-offer F8
                UpdateTitle(state.Model);
                _chat.AddLine($"[dim][[new job started — model: {Markup.Escape(state.Model)}]][/]");
                Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
                return true;
            case "/jobs":
                var jobsList = await store.ListAsync(20, ct).ConfigureAwait(false);
                foreach (var j in jobsList)
                    _chat.AddLine($"[grey]{Markup.Escape(j.Id)}  {j.Status}  {Markup.Escape(j.Model ?? "")}  updated={j.UpdatedAt:u}[/]");
                Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
                return true;
            case "/resume":
                if (parts.Length < 2) { _chat.AddLine("[yellow]usage: /resume <jobId> (or press F3)[/]"); Render(ctx, layout, state, openAi, mcpHost, builtIns, input); return true; }
                var loaded = await store.LoadAsync(parts[1].Trim(), ct).ConfigureAwait(false);
                if (loaded is null) { _chat.AddLine($"[yellow][[no such job: {Markup.Escape(parts[1])}]][/]"); Render(ctx, layout, state, openAi, mcpHost, builtIns, input); return true; }
                state.Id = loaded.Id;
                state.ParentId = loaded.ParentId;
                state.Model = loaded.Model;
                state.SystemPrompt = loaded.SystemPrompt;
                state.Depth = loaded.Depth;
                state.Status = loaded.Status;
                state.ApproxTokenCount = loaded.ApproxTokenCount;
                state.TurnsTaken = loaded.TurnsTaken;
                state.TotalInputTokens = loaded.TotalInputTokens;
                state.TotalOutputTokens = loaded.TotalOutputTokens;
                state.TotalCostUsd = loaded.TotalCostUsd;
                state.CreatedAt = loaded.CreatedAt;
                state.UpdatedAt = loaded.UpdatedAt;
                state.History = loaded.History;
                state.WorkingDirectory = loaded.WorkingDirectory;
                _firstTurnStarted = true; // resumed → no longer a fresh session
                UpdateTitle(state.Model);
                _chat.AddLine($"[dim][[resumed job {Markup.Escape(state.Id)} ({state.History.Count} messages, ~{state.ApproxTokenCount} tokens)]][/]");
                Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
                return true;
            case "/compress":
                var compressor = _services.GetRequiredService<ContextCompressor>();
                await compressor.CompressAsync(state, ct).ConfigureAwait(false);
                _chat.AddLine($"[dim][[compressed to {state.History.Count} messages]][/]");
                Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
                return true;
            case "/help":
                ShowSlashCommandMenu();
                Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
                return true;
            default:
                _chat.AddLine($"[yellow][[unknown command: {Markup.Escape(cmd)}]][/]");
                Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
                return true;
        }
    }

    /// <summary>
    /// Scan recent jobs newest → oldest and load the first one whose
    /// <see cref="ConversationState.WorkingDirectory"/> matches the current launch
    /// directory. Used by F8 from the fresh-session header.
    /// </summary>
    private async Task ResumeLastInCwdAsync(ConversationState state, IJobStore store,
        LiveDisplayContext ctx, Layout layout, OpenAIOptions openAi,
        McpClientHost mcpHost, BuiltInToolRegistry builtIns, InputState input,
        CancellationToken ct)
    {
        var launchInfo = _services.GetRequiredService<Configuration.HostLaunchInfo>();
        var cwd = launchInfo.OriginalWorkingDirectory;

        var jobs = await store.ListAsync(100, ct).ConfigureAwait(false);
        ConversationState? match = null;
        foreach (var j in jobs)
        {
            if (j.Id == state.Id) continue; // skip the just-created empty session
            ConversationState? loaded;
            try { loaded = System.Text.Json.JsonSerializer.Deserialize<ConversationState>(j.StateJson); }
            catch (Exception ex) { _log.LogDebug(ex, "F8: failed to deserialise job {Id}", j.Id); continue; }
            if (loaded is null) continue;
            if (!string.Equals(loaded.WorkingDirectory, cwd, StringComparison.OrdinalIgnoreCase)) continue;
            match = loaded;
            break; // jobs are ordered DESC by updated_at, so the first match is the most recent
        }

        if (match is null)
        {
            _chat.AddLine($"[yellow][[F8: no previous session found in {Markup.Escape(cwd)}]][/]");
            Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
            return;
        }

        // Same swap logic as /resume — copy the loaded job's state into our running
        // ConversationState reference. Bumps _firstTurnStarted so F8 doesn't re-offer.
        state.Id = match.Id;
        state.ParentId = match.ParentId;
        state.Model = match.Model;
        state.SystemPrompt = match.SystemPrompt;
        state.Depth = match.Depth;
        state.Status = match.Status;
        state.ApproxTokenCount = match.ApproxTokenCount;
        state.TurnsTaken = match.TurnsTaken;
        state.TotalInputTokens = match.TotalInputTokens;
        state.TotalOutputTokens = match.TotalOutputTokens;
        state.TotalCostUsd = match.TotalCostUsd;
        state.CreatedAt = match.CreatedAt;
        state.UpdatedAt = match.UpdatedAt;
        state.History = match.History;
        state.WorkingDirectory = match.WorkingDirectory;
        _firstTurnStarted = true;
        UpdateTitle(state.Model);
        _chat.AddLine($"[dim][[F8 resumed job {Markup.Escape(state.Id)} from {Markup.Escape(cwd)} — {state.History.Count} messages]][/]");
        Render(ctx, layout, state, openAi, mcpHost, builtIns, input);
    }

    private void ShowSlashCommandMenu()
    {
        _chat.AddLine("[grey]── slash commands ──[/]");
        foreach (var (name, description) in SlashCommands)
            _chat.AddLine($"  [red]{Markup.Escape(name).PadRight(18)}[/] [dim]{Markup.Escape(description)}[/]");

        _chat.AddLine("");
        _chat.AddLine("[grey]── key bindings ──[/]");
        foreach (var (keys, description) in KeyBindings)
            _chat.AddLine($"  [red]{Markup.Escape(keys).PadRight(18)}[/] [dim]{Markup.Escape(description)}[/]");
    }

    private static readonly (string Keys, string Description)[] KeyBindings = new[]
    {
        ("Enter",            "Send the current input (queues if a turn is in flight)."),
        ("Shift+Enter",      "Insert a newline into the input (also handles multi-line paste)."),
        ("↑ / ↓",            "Move cursor between lines in multi-line input."),
        ("Ctrl+↑ / Ctrl+↓",  "Navigate previous / next command from history."),
        ("← / →",            "Move cursor one character."),
        ("Ctrl+← / Ctrl+→",  "Move cursor one word."),
        ("Home / End",       "Jump to start / end of the buffer."),
        ("Shift+← Shift+→",  "Extend selection one character (or word with Ctrl)."),
        ("Shift+Home / End", "Extend selection to start / end."),
        ("Ctrl+A",           "Select all."),
        ("Ctrl+C (selected)", "Copy selection to clipboard."),
        ("Ctrl+C (streaming)", "Cancel the in-flight agent turn."),
        ("Ctrl+C (idle)",    "Open exit confirmation (Ctrl+C again to close)."),
        ("Ctrl+W",           "Delete previous word (or selection)."),
        ("Ctrl+U / Esc",     "Clear the input."),
        ("Ctrl+L",           "Clear the chat scrollback."),
        ("PageUp / PageDown", "Scroll chat history. Stays locked while new lines arrive; auto-follows at the bottom."),
        ("F1",               "Show job / model / endpoint info."),
        ("F2",               "Show this menu."),
        ("F3",               "Pick a past session to /resume."),
        ("F8 (fresh only)",  "Resume the most recent job started in this working directory."),
    };

    private void ShowSessionPickerInChat(IReadOnlyList<JobRecord> jobs)
    {
        _chat.AddLine("[grey]── recent sessions — copy an id into the prefilled /resume command ──[/]");
        if (jobs.Count == 0) { _chat.AddLine("[dim]  (no past sessions yet)[/]"); return; }
        foreach (var j in jobs)
        {
            _chat.AddLine($"  [red]{Markup.Escape(j.Id)}[/]  [dim]{j.Status,-9}  {Markup.Escape(j.Model ?? ""),-28}  updated={j.UpdatedAt:u}[/]");
        }
    }

    // ──────────────────────────── stdin-redirected mode ────────────────────────────

    private async Task<int> RunRedirectedAsync(LlmAgent agent, ConversationState state, CancellationToken ct)
    {
        var input = Console.In;
        while (!ct.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return 0;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var response = await agent.RunTurnAsync(state, line, ct).ConfigureAwait(false);
                Console.WriteLine(response.Text);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Redirected turn failed");
                Console.Error.WriteLine($"[error] {ex.Message}");
            }
        }
        return 0;
    }

    private static void UpdateTitle(string model)
    {
        try { Console.Title = $"Dagger - {model}"; } catch { /* not a TTY */ }
    }

    /// <summary>
    /// Best-effort cross-platform clipboard write via the OS's built-in copy tool.
    /// Windows uses clip.exe, macOS uses pbcopy, Linux tries wl-copy / xclip / xsel
    /// in that order. Returns false if no tool was found or the process failed.
    /// </summary>
    private static bool TryCopyToClipboard(string text)
    {
        try
        {
            var (file, args) = ResolveClipboardCmd();
            if (file is null) return false;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return false;
            proc.StandardInput.Write(text);
            proc.StandardInput.Close();
            if (!proc.WaitForExit(2000)) { try { proc.Kill(); } catch { } return false; }
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private static (string? File, string Args) ResolveClipboardCmd()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            return ("clip.exe", "");
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
            return ("pbcopy", "");
        // Linux: prefer Wayland (wl-copy), fall back to X11 (xclip, xsel).
        if (FindOnPath("wl-copy") is not null) return ("wl-copy", "");
        if (FindOnPath("xclip") is not null)   return ("xclip", "-selection clipboard");
        if (FindOnPath("xsel") is not null)    return ("xsel", "--clipboard --input");
        return (null, "");
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    private static int SafeGetCursorLeft()    { try { return Console.CursorLeft; }    catch { return -1; } }
    private static int SafeGetCursorTop()     { try { return Console.CursorTop; }     catch { return -1; } }
    private static int SafeGetWindowLeft()    { try { return Console.WindowLeft; }    catch { return -1; } }
    private static int SafeGetWindowTop()     { try { return Console.WindowTop; }     catch { return -1; } }
    private static int SafeGetBufferWidth()   { try { return Console.BufferWidth; }   catch { return -1; } }
    private static int SafeGetBufferHeight()  { try { return Console.BufferHeight; }  catch { return -1; } }
    private static int SafeGetWindowWidth()   { try { return Console.WindowWidth; }   catch { return -1; } }
    private static int SafeGetWindowHeight()  { try { return Console.WindowHeight; }  catch { return -1; } }

    // ──────────────────────────── nested types ────────────────────────────

    /// <summary>
    /// Append-only chat content rendered into the middle of the TUI. Supports a single
    /// <em>pending</em> line that gets rewritten on every streaming token; once the
    /// streaming chunk is done we commit it into the immutable line list.
    /// </summary>
    internal sealed class ChatBuffer
    {
        private readonly List<string> _lines = new();
        private string? _pending;

        public void AddLine(string markup)
        {
            _lines.Add(markup);
            if (_lines.Count > ChatScrollback) _lines.RemoveRange(0, _lines.Count - ChatScrollback);
        }

        public void BeginPending() => _pending = "";
        public void UpdatePending(string markup) => _pending = markup;
        public void CommitPending()
        {
            if (_pending is not null && _pending.Length > 0) AddLine(_pending);
            _pending = null;
        }

        public void Clear() { _lines.Clear(); _pending = null; }

        /// <summary>
        /// Return the last <paramref name="maxLines"/> rendered lines (line = pending counts
        /// as one) for the chat panel. Older lines past the cap are dropped.
        /// </summary>
        public List<string> RecentLines(int maxLines)
        {
            var combined = new List<string>(_lines.Count + 1);
            combined.AddRange(_lines);
            if (_pending is not null) combined.Add(_pending);
            if (combined.Count <= maxLines) return combined;
            return combined.GetRange(combined.Count - maxLines, maxLines);
        }

        /// <summary>All source lines (with the in-progress pending line at the end if any).
        /// Used by <see cref="BuildChat"/> which walks backward and word-wraps each line,
        /// so we can't just take the last N — we need to be able to keep going.</summary>
        public List<string> AllLines()
        {
            var combined = new List<string>(_lines.Count + 1);
            combined.AddRange(_lines);
            if (_pending is not null) combined.Add(_pending);
            return combined;
        }
    }

    internal sealed class InputState
    {
        public StringBuilder Buffer { get; } = new();
        public int Cursor { get; set; }
        /// <summary>
        /// Anchor end of a selection range; <see cref="Cursor"/> is the other end.
        /// null when nothing is selected. The visible selection is the half-open
        /// range [min(Anchor, Cursor), max(Anchor, Cursor)).
        /// </summary>
        public int? SelectionAnchor { get; set; }
        public int HistoryIdx { get; set; } = -1;
        public string? HistoryDraft { get; set; }
        /// <summary>
        /// First buffer column visible in the input row. Mutated by <see cref="BuildInput"/>
        /// each render so the cursor stays inside the visible window as the user types or
        /// moves left/right. Reset to 0 by <see cref="Clear"/>.
        /// </summary>
        public int ScrollOffset { get; set; }

        public bool HasSelection => SelectionAnchor.HasValue && SelectionAnchor.Value != Cursor;

        public (int Start, int End) SelectionRange
        {
            get
            {
                if (!SelectionAnchor.HasValue) return (Cursor, Cursor);
                var a = SelectionAnchor.Value;
                return a < Cursor ? (a, Cursor) : (Cursor, a);
            }
        }

        // ─── multi-line helpers ───
        // Buffer can contain '\n' (inserted via Shift+Enter, or coming in from a paste).
        // These helpers treat '\n' as a hard line break.

        /// <summary>Index of the first character on the line containing <paramref name="pos"/>.</summary>
        public int LineStartAt(int pos)
        {
            var i = Math.Min(pos, Buffer.Length) - 1;
            while (i >= 0 && Buffer[i] != '\n') i--;
            return i + 1;
        }

        /// <summary>Index just past the last character on the line containing <paramref name="pos"/>
        /// (i.e. index of the next '\n' or buffer length).</summary>
        public int LineEndAt(int pos)
        {
            var i = Math.Max(0, pos);
            while (i < Buffer.Length && Buffer[i] != '\n') i++;
            return i;
        }

        /// <summary>Zero-based row + column of <see cref="Cursor"/> in the buffer.</summary>
        public (int Row, int Col) CursorRowCol
        {
            get
            {
                int row = 0, lineStart = 0;
                for (var i = 0; i < Cursor; i++)
                {
                    if (Buffer[i] == '\n') { row++; lineStart = i + 1; }
                }
                return (row, Cursor - lineStart);
            }
        }

        /// <summary>1-based number of lines (1 for empty buffer; one more for each '\n').</summary>
        public int LineCount
        {
            get
            {
                var n = 1;
                for (var i = 0; i < Buffer.Length; i++) if (Buffer[i] == '\n') n++;
                return n;
            }
        }

        /// <summary>Convert a (row, col) pair back to a flat buffer index. Clamps col to the
        /// target row's actual length; clamps row to 0..LineCount-1.</summary>
        public int RowColToIndex(int row, int col)
        {
            if (row < 0) return 0;
            int currentRow = 0, lineStart = 0;
            for (var i = 0; i <= Buffer.Length; i++)
            {
                if (currentRow == row)
                {
                    var lineEnd = LineEndAt(lineStart);
                    return Math.Min(lineStart + Math.Max(0, col), lineEnd);
                }
                if (i == Buffer.Length) break;
                if (Buffer[i] == '\n') { currentRow++; lineStart = i + 1; }
            }
            return Buffer.Length;
        }

        /// <summary>Removes the currently-selected range from the buffer and parks the
        /// cursor at the start of where the range was. No-op when nothing is selected.</summary>
        public void DeleteSelection()
        {
            if (!HasSelection) return;
            var (start, end) = SelectionRange;
            Buffer.Remove(start, end - start);
            Cursor = start;
            SelectionAnchor = null;
        }

        public void Clear()
        {
            Buffer.Clear();
            Cursor = 0;
            SelectionAnchor = null;
            HistoryIdx = -1;
            HistoryDraft = null;
            ScrollOffset = 0;
        }
    }
}
