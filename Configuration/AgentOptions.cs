namespace Daggeragent.Configuration;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public string SystemPrompt { get; set; } = "You are DaggerAgent, a focused LLM coding/task agent.";

    /// <summary>
    /// Filename consulted in the working directory at session start. If present, its
    /// contents are appended to the system prompt under a "Project context" section.
    /// Absent file is silently ignored.
    /// </summary>
    public string PersonalityFile { get; set; } = "dagger.md";

    /// <summary>
    /// Model used to summarise older history during context compression. Often a cheaper
    /// model than the main one (e.g. an 8B model for compression while a 70B drives the
    /// conversation). When empty, falls back to OpenAI.DefaultModel.
    /// </summary>
    public string SummariserModel { get; set; } = "";
    public int MaxContextTokens { get; set; } = 120000;
    public int CompressionThreshold { get; set; } = 90000;
    public int CompressionKeepLastTurns { get; set; } = 6;
    public int MaxTurnsPerInvocation { get; set; } = 25;
    public int MaxSubAgentDepth { get; set; } = 3;
    public int MaxTurnsPerSubAgent { get; set; } = 10;

    /// <summary>
    /// When true (default), &lt;think&gt;...&lt;/think&gt; blocks emitted by reasoning models
    /// (Qwen3-thinking, DeepSeek-R1, etc.) are stripped from persisted history. Saves context
    /// tokens on subsequent turns. Set false to keep them — useful if you need to audit
    /// the model's reasoning later.
    /// </summary>
    public bool HideThinkingFromHistory { get; set; } = true;

    /// <summary>
    /// When true (default), thinking content is rendered dim/grey in interactive mode so the
    /// reasoning is visible but visually distinct from the final answer.
    /// </summary>
    public bool ShowThinking { get; set; } = true;

    /// <summary>
    /// Compact one-line per-turn stats appended after the assistant reply in interactive mode:
    /// e.g. "[1.7k tk, 1.3s, 245 tkps]". Default true. Set false to hide entirely.
    /// </summary>
    public bool ShowTurnStats { get; set; } = true;

    /// <summary>
    /// In interactive mode, surface tool calls inline as "→ name(args)" lines and tool
    /// results as "← excerpt" lines (max ~100 chars, control chars sanitised). Default true.
    /// </summary>
    public bool ShowToolCalls { get; set; } = true;

    /// <summary>
    /// Full structured per-turn telemetry logged via Serilog at Information level:
    /// "LLM turn complete: job=... model=... input_tokens=... ...". Off by default
    /// (was on previously) — set true to bring back the old, verbose format. Independent
    /// of ShowTurnStats; the file-sink record is unaffected when off (it logs at Debug instead).
    /// </summary>
    public bool VerboseTurnStats { get; set; } = false;

    /// <summary>
    /// When true (default), append a "Runtime context" block to the system prompt with the
    /// host OS, architecture, .NET runtime version, hostname, working directory, path
    /// separator, and recommended shell. Stops the agent from guessing the platform and
    /// reaching for the wrong shell (e.g. bash on Windows).
    /// </summary>
    public bool IncludeRuntimeContext { get; set; } = true;

    /// <summary>
    /// When true (default), if a turn finishes with a non-stop finish_reason (typically
    /// "length" = hit max_tokens, or no reason at all = stream cut off) the agent
    /// automatically fires a continuation turn asking the model to resume from where it
    /// left off. Capped by <see cref="MaxAutoContinues"/> per user submission so a model
    /// that keeps stopping doesn't loop forever.
    /// </summary>
    public bool AutoContinueOnIncomplete { get; set; } = true;

    /// <summary>Maximum auto-continue retries per user submission.</summary>
    public int MaxAutoContinues { get; set; } = 2;
}
