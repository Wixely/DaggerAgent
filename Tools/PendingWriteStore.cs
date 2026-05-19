using System.Collections.Concurrent;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace Daggeragent.Tools;

public sealed class PendingChange
{
    public required string AbsolutePath { get; init; }
    public required string DisplayPath { get; init; }
    public required string OldContent { get; init; }
    public required string NewContent { get; init; }
    public DateTimeOffset StagedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class PendingWriteStore
{
    private readonly ConcurrentDictionary<string, PendingChange> _pending = new(StringComparer.OrdinalIgnoreCase);

    public void Stage(PendingChange change) => _pending[change.AbsolutePath] = change;

    public PendingChange? Get(string absolutePath) =>
        _pending.TryGetValue(absolutePath, out var c) ? c : null;

    public bool Remove(string absolutePath) => _pending.TryRemove(absolutePath, out _);

    public IReadOnlyCollection<PendingChange> All() => _pending.Values.ToList();

    public int Count => _pending.Count;

    public static string RenderUnifiedDiff(string path, string oldContent, string newContent)
    {
        var diff = InlineDiffBuilder.Diff(oldContent, newContent, ignoreWhiteSpace: false);
        var sb = new System.Text.StringBuilder();
        sb.Append("--- a/").AppendLine(path);
        sb.Append("+++ b/").AppendLine(path);
        var oldLine = 0;
        var newLine = 0;
        foreach (var line in diff.Lines)
        {
            switch (line.Type)
            {
                case ChangeType.Inserted:
                    newLine++;
                    sb.Append("+ ").AppendLine(line.Text);
                    break;
                case ChangeType.Deleted:
                    oldLine++;
                    sb.Append("- ").AppendLine(line.Text);
                    break;
                case ChangeType.Unchanged:
                    oldLine++;
                    newLine++;
                    sb.Append("  ").AppendLine(line.Text);
                    break;
                case ChangeType.Modified:
                    oldLine++;
                    newLine++;
                    sb.Append("~ ").AppendLine(line.Text);
                    break;
            }
        }
        return sb.ToString();
    }
}
