using System.Collections.Concurrent;

namespace Daggeragent.Tools;

/// <summary>
/// In-memory holder for tool results too large to inline into the model's context. Keyed
/// by (jobId, resultId). LRU-evicts beyond a per-job cap so a chatty turn can't blow
/// process memory. Not persisted — restarting the server loses every offload.
/// </summary>
public sealed class ToolResultStore
{
    private const int PerJobCap = 32;

    public sealed record Entry(
        string ResultId,
        string ToolName,
        string Content,
        DateTimeOffset SavedAt);

    private readonly ConcurrentDictionary<(string jobId, string resultId), Entry> _byKey = new();
    private readonly ConcurrentDictionary<string, LinkedList<string>> _orderByJob = new();

    public Entry Save(string jobId, string toolName, string content)
    {
        var id = "tr_" + Guid.NewGuid().ToString("N")[..8];
        var entry = new Entry(id, toolName, content, DateTimeOffset.UtcNow);
        _byKey[(jobId, id)] = entry;

        var order = _orderByJob.GetOrAdd(jobId, _ => new LinkedList<string>());
        lock (order)
        {
            order.AddLast(id);
            while (order.Count > PerJobCap)
            {
                var oldest = order.First!.Value;
                order.RemoveFirst();
                _byKey.TryRemove((jobId, oldest), out _);
            }
        }
        return entry;
    }

    public Entry? Get(string jobId, string resultId) =>
        _byKey.TryGetValue((jobId, resultId), out var e) ? e : null;

    public IReadOnlyList<Entry> List(string jobId)
    {
        if (!_orderByJob.TryGetValue(jobId, out var order)) return Array.Empty<Entry>();
        lock (order)
        {
            var list = new List<Entry>(order.Count);
            foreach (var id in order)
            {
                if (_byKey.TryGetValue((jobId, id), out var e)) list.Add(e);
            }
            return list;
        }
    }
}
