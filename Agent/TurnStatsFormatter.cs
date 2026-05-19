namespace Daggeragent.Agent;

/// <summary>
/// Compact end-of-turn summary, e.g. <c>[1.7k tk, 1.3s, 245 tkps, 3 calls]</c>.
/// Total duration is wall-clock; tokens-per-second is computed over the
/// <em>LLM-only</em> elapsed (total minus time spent waiting on tool/MCP calls)
/// so slow tools don't drag the apparent generation speed down.
/// </summary>
public static class TurnStatsFormatter
{
    public static string Format(long totalTokens, long outputTokens, long elapsedMs, long llmElapsedMs, int toolCalls)
    {
        var kTok = totalTokens / 1000.0;
        var tokenStr = kTok < 0.05 ? "0.0k tk" : $"{kTok:F1}k tk";

        var dur = FormatDuration(elapsedMs);
        var tkpsBasis = llmElapsedMs > 0 ? llmElapsedMs : elapsedMs;
        var tkps = tkpsBasis > 0 ? (int)Math.Round(outputTokens * 1000.0 / tkpsBasis) : 0;

        return $"[{tokenStr}, {dur}, {tkps} tkps, {toolCalls} {(toolCalls == 1 ? "call" : "calls")}]";
    }

    private static string FormatDuration(long ms)
    {
        if (ms < 1000) return $"{ms}ms";
        if (ms < 60_000) return $"{ms / 1000.0:F1}s";
        var totalSec = ms / 1000;
        return $"{totalSec / 60}m {totalSec % 60}s";
    }
}
