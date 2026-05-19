using System.Text;

namespace Daggeragent.Modes;

/// <summary>
/// Renders tool call / result info for interactive display, sanitising any control
/// characters so a tool result containing ANSI escape sequences, NULs, carriage returns,
/// or terminal control codes can't garble the terminal.
/// </summary>
internal static class ToolCallFormatter
{
    /// <summary>
    /// Truncate to <paramref name="maxChars"/> printable characters (counting after
    /// substitution) and escape every C0/C1 control char. Multi-line whitespace collapses
    /// to literal "\n" / "\t" so the excerpt always renders on a single line.
    /// </summary>
    public static string SafeExcerpt(string? s, int maxChars)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, maxChars) + 2);
        var n = 0;
        for (var i = 0; i < s.Length && n < maxChars; i++)
        {
            var c = s[i];
            if (c == '\n') { sb.Append("\\n"); n += 2; }
            else if (c == '\r') { sb.Append("\\r"); n += 2; }
            else if (c == '\t') { sb.Append("\\t"); n += 2; }
            else if (c == 0x1B) { sb.Append("\\e"); n += 2; }     // ESC — would otherwise start an ANSI sequence
            else if (char.IsControl(c)) { sb.Append('·'); n++; }   // any other C0/C1
            else { sb.Append(c); n++; }
        }
        if (n >= maxChars && sb.Length < s.Length) sb.Append('…');
        return sb.ToString();
    }

    /// <summary>Render a tool's args dictionary compactly, e.g. <c>path="src/x.cs", lines=10</c>.</summary>
    public static string FormatArgs(IDictionary<string, object?>? args, int maxTotalChars = 80)
    {
        if (args is null || args.Count == 0) return "";
        var parts = new List<string>(args.Count);
        foreach (var kv in args)
        {
            var value = kv.Value;
            string rendered = value switch
            {
                null               => "null",
                bool b             => b ? "true" : "false",
                string s           => $"\"{SafeExcerpt(s, 30)}\"",
                int or long or double or float or decimal => value.ToString() ?? "",
                _                  => $"\"{SafeExcerpt(value.ToString() ?? "", 30)}\"",
            };
            parts.Add($"{kv.Key}={rendered}");
        }
        var joined = string.Join(", ", parts);
        return joined.Length > maxTotalChars ? joined.Substring(0, maxTotalChars) + "…" : joined;
    }
}
