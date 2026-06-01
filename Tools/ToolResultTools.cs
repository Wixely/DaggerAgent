using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Daggeragent.Tools;

/// <summary>
/// Tools the model uses to consume content that <see cref="OffloadingAIFunction"/>
/// stashed into <see cref="ToolResultStore"/>. Read-only; jail-free; jobId-scoped.
/// </summary>
public sealed class ToolResultTools
{
    /// <summary>
    /// Tools in this set must NOT be wrapped with <see cref="OffloadingAIFunction"/> —
    /// otherwise reading a 16K slice would recursively offload its own response.
    /// </summary>
    public static readonly IReadOnlySet<string> ConsumerToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "read_tool_result",
        "head_tool_result",
        "tail_tool_result",
        "grep_tool_result",
        "list_tool_results",
    };

    private readonly ToolResultStore _store;

    public ToolResultTools(ToolResultStore store) { _store = store; }

    public IEnumerable<AITool> Build(string? jobId)
    {
        var safeJobId = jobId ?? "";

        string ReadToolResult(
            [Description("Result id returned by an offloaded tool, e.g. 'tr_a1b2c3d4'.")] string id,
            [Description("Starting character offset into the stored content. Default 0.")] int offset = 0,
            [Description("Max characters to return. Default 2000, hard cap 32000.")] int limit = 2000)
        {
            var entry = _store.Get(safeJobId, id);
            if (entry is null) return MissingMessage(id);
            return Slice(entry.Content, offset, limit);
        }

        string HeadToolResult(
            [Description("Result id returned by an offloaded tool.")] string id,
            [Description("Number of leading lines to return. Default 50.")] int lines = 50)
        {
            var entry = _store.Get(safeJobId, id);
            if (entry is null) return MissingMessage(id);
            return HeadLines(entry.Content, lines);
        }

        string TailToolResult(
            [Description("Result id returned by an offloaded tool.")] string id,
            [Description("Number of trailing lines to return. Default 50.")] int lines = 50)
        {
            var entry = _store.Get(safeJobId, id);
            if (entry is null) return MissingMessage(id);
            return TailLines(entry.Content, lines);
        }

        string GrepToolResult(
            [Description("Result id returned by an offloaded tool.")] string id,
            [Description(".NET regex pattern to match line-by-line. Case-insensitive.")] string pattern,
            [Description("Surrounding context lines around each match. Default 0.")] int contextLines = 0,
            [Description("Max matches to return. Default 200.")] int maxMatches = 200)
        {
            var entry = _store.Get(safeJobId, id);
            if (entry is null) return MissingMessage(id);
            return Grep(entry.Content, pattern, contextLines, maxMatches);
        }

        string ListToolResults()
        {
            var rows = _store.List(safeJobId);
            if (rows.Count == 0) return "(no stashed tool results in this job)";
            var sb = new StringBuilder();
            foreach (var r in rows)
            {
                sb.Append(r.ResultId)
                  .Append("  ").Append(r.Content.Length.ToString("N0")).Append(" chars")
                  .Append("  from=").Append(r.ToolName)
                  .Append("  saved=").Append(r.SavedAt.ToString("u"))
                  .AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        yield return AIFunctionFactory.Create(ReadToolResult, name: "read_tool_result", description:
            "Read a slice of a previously offloaded tool result. Returns content[offset..offset+limit]. " +
            "Use this when a tool returned an 'Offload' placeholder and you need to consume specific portions of the full payload.");
        yield return AIFunctionFactory.Create(HeadToolResult, name: "head_tool_result", description:
            "Return the first N lines of a previously offloaded tool result.");
        yield return AIFunctionFactory.Create(TailToolResult, name: "tail_tool_result", description:
            "Return the last N lines of a previously offloaded tool result.");
        yield return AIFunctionFactory.Create(GrepToolResult, name: "grep_tool_result", description:
            "Search a previously offloaded tool result for a regex (case-insensitive, line-by-line). Returns matching lines with line numbers and optional surrounding context.");
        yield return AIFunctionFactory.Create(ListToolResults, name: "list_tool_results", description:
            "List all stashed tool results for this job — id, size, originating tool, save time.");
    }

    private static string MissingMessage(string id) =>
        $"Error: no stashed tool result with id '{id}'. Call list_tool_results() to see what's available.";

    private static string Slice(string content, int offset, int limit)
    {
        if (offset < 0) offset = 0;
        if (offset >= content.Length) return $"(end of result — content is {content.Length} chars, offset {offset} is past the end)";
        var hardCap = Math.Min(Math.Max(1, limit), 32_000);
        var take = Math.Min(hardCap, content.Length - offset);
        var slice = content.Substring(offset, take);
        var trailer = offset + take < content.Length
            ? $"\n\n[…{content.Length - offset - take:N0} more chars. Call read_tool_result with offset={offset + take} to continue.]"
            : "\n\n[end of result]";
        return slice + trailer;
    }

    private static string HeadLines(string content, int linesWanted)
    {
        if (linesWanted <= 0) linesWanted = 50;
        var sb = new StringBuilder();
        using var reader = new StringReader(content);
        int n = 0;
        string? line;
        while (n < linesWanted && (line = reader.ReadLine()) is not null)
        {
            sb.AppendLine(line);
            n++;
        }
        return sb.ToString();
    }

    private static string TailLines(string content, int linesWanted)
    {
        if (linesWanted <= 0) linesWanted = 50;
        var lines = content.Split('\n');
        var start = Math.Max(0, lines.Length - linesWanted);
        return string.Join('\n', lines, start, lines.Length - start);
    }

    private static string Grep(string content, string pattern, int contextLines, int maxMatches)
    {
        if (string.IsNullOrEmpty(pattern)) return "Error: pattern is required.";
        Regex regex;
        try { regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException ex) { return $"Error: invalid regex — {ex.Message}"; }

        var lines = content.Split('\n');
        var sb = new StringBuilder();
        var matches = 0;
        for (var i = 0; i < lines.Length && matches < maxMatches; i++)
        {
            bool isMatch;
            try { isMatch = regex.IsMatch(lines[i]); }
            catch (RegexMatchTimeoutException) { return $"Error: regex match timed out (pattern too expensive on this content)."; }
            if (!isMatch) continue;
            matches++;
            var lo = Math.Max(0, i - contextLines);
            var hi = Math.Min(lines.Length - 1, i + contextLines);
            for (var k = lo; k <= hi; k++)
            {
                var marker = k == i ? ":" : "-";
                sb.Append(k + 1).Append(marker).Append(' ').AppendLine(lines[k]);
            }
            if (contextLines > 0 && hi < lines.Length - 1) sb.AppendLine("--");
        }
        if (matches == 0) return "(no matches)";
        if (matches == maxMatches) sb.Append("[truncated at ").Append(maxMatches).Append(" matches]");
        return sb.ToString().TrimEnd();
    }
}
