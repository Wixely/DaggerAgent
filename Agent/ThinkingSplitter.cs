using System.Text;

namespace Daggeragent.Agent;

/// <summary>
/// State machine that classifies streamed text as either "thinking" (inside &lt;think&gt;...&lt;/think&gt;
/// blocks) or "answer" (everything else). Handles tag boundaries that fall across chunk
/// boundaries — e.g. one update ending with "&lt;thi" and the next starting with "nk&gt;" — by
/// holding back text that could be the start of a tag until the next chunk arrives or the
/// stream ends.
/// </summary>
public sealed class ThinkingSplitter
{
    public const string OpenTag = "<think>";
    public const string CloseTag = "</think>";

    private readonly StringBuilder _buffer = new();
    private bool _inThinking;

    public bool InThinking => _inThinking;

    /// <summary>
    /// Push the next chunk of streamed text. Yields classified segments. A segment may have
    /// empty text — caller should ignore those.
    /// </summary>
    public IEnumerable<Segment> Push(string? chunk)
    {
        if (string.IsNullOrEmpty(chunk)) yield break;
        _buffer.Append(chunk);

        while (_buffer.Length > 0)
        {
            var content = _buffer.ToString();
            var target = _inThinking ? CloseTag : OpenTag;
            var idx = content.IndexOf(target, StringComparison.Ordinal);

            if (idx >= 0)
            {
                // Complete tag found. Emit text before it under the current classification,
                // then flip and continue parsing the remainder.
                var before = content.Substring(0, idx);
                if (before.Length > 0) yield return new Segment(before, _inThinking);
                _buffer.Remove(0, idx + target.Length);
                _inThinking = !_inThinking;
                continue;
            }

            // No complete tag. Check if the tail could be the start of one — if so, hold it back.
            var holdback = PartialTailMatch(content, target);
            if (holdback > 0)
            {
                var safe = content.Length - holdback;
                if (safe > 0)
                {
                    yield return new Segment(content.Substring(0, safe), _inThinking);
                    _buffer.Remove(0, safe);
                }
                yield break;
            }

            // Tail is definitely not a partial tag — emit the whole buffer.
            yield return new Segment(content, _inThinking);
            _buffer.Clear();
        }
    }

    /// <summary>
    /// Drain any buffered content at the end of the stream. Classified under the current mode.
    /// </summary>
    public Segment? Flush()
    {
        if (_buffer.Length == 0) return null;
        var text = _buffer.ToString();
        _buffer.Clear();
        return new Segment(text, _inThinking);
    }

    /// <summary> Returns the length of the longest suffix of `s` that is a prefix of `tag`. </summary>
    private static int PartialTailMatch(string s, string tag)
    {
        var max = Math.Min(tag.Length - 1, s.Length);
        for (var n = max; n > 0; n--)
        {
            if (tag.AsSpan(0, n).SequenceEqual(s.AsSpan(s.Length - n))) return n;
        }
        return 0;
    }

    /// <summary>
    /// Convenience: strip all &lt;think&gt;...&lt;/think&gt; blocks from a fully-formed string
    /// (no streaming). Used during persistence to keep history clean.
    /// </summary>
    public static string StripThinking(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        var s = new ThinkingSplitter();
        foreach (var seg in s.Push(text))
        {
            if (!seg.IsThinking) sb.Append(seg.Text);
        }
        var tail = s.Flush();
        if (tail is { IsThinking: false }) sb.Append(tail.Value.Text);
        return sb.ToString().TrimStart();
    }

    /// <summary>
    /// Returns the concatenated thinking content from a fully-formed string.
    /// </summary>
    public static string ExtractThinking(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder();
        var s = new ThinkingSplitter();
        foreach (var seg in s.Push(text))
        {
            if (seg.IsThinking) sb.Append(seg.Text);
        }
        var tail = s.Flush();
        if (tail is { IsThinking: true }) sb.Append(tail.Value.Text);
        return sb.ToString();
    }

    public readonly record struct Segment(string Text, bool IsThinking);
}
