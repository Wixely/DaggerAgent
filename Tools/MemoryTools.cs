using System.ComponentModel;
using System.Text;
using Daggeragent.Configuration;
using Daggeragent.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Daggeragent.Tools;

public sealed class MemoryTools
{
    private readonly MemoryStore _store;
    private readonly ToolsOptions _toolsOptions;

    public MemoryTools(MemoryStore store, IOptions<ToolsOptions> toolsOptions)
    {
        _store = store;
        _toolsOptions = toolsOptions.Value;
    }

    public IEnumerable<AITool> Build(string? jobId)
    {
        if (!_store.Enabled) yield break;

        yield return AIFunctionFactory.Create(
            (string query, int? limit, CancellationToken ct) => RecallPastWork(query, limit, ct),
            name: "recall_past_work",
            description: "Search the agent's long-term memory of past conversations for entries relevant to a query. Returns the top matches with similarity scores. Use this when the user references prior work or you need context from earlier sessions.");

        if (!_toolsOptions.ReadOnly)
        {
            yield return AIFunctionFactory.Create(
                (string text, CancellationToken ct) => Remember(jobId, text, ct),
                name: "remember",
                description: "Save a fact, decision, or summary to long-term memory so it can be recalled in future sessions. Use sparingly — only for information that would help future-you, not transient details.");
        }
    }

    [Description("Recall past work.")]
    private async Task<string> RecallPastWork(
        [Description("Free-text query describing what to recall.")] string query,
        [Description("Maximum number of memories to return. Defaults to the server's configured limit.")] int? limit,
        CancellationToken ct)
    {
        var hits = await _store.RecallAsync(query, limit, ct).ConfigureAwait(false);
        if (hits.Count == 0) return "(no relevant memories)";

        var sb = new StringBuilder();
        for (var i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            sb.Append('[').Append(i + 1).Append("] sim=").AppendFormat("{0:F2}", h.Similarity)
              .Append("  ts=").Append(h.Record.CreatedAt.ToString("u"))
              .AppendLine();
            sb.AppendLine(h.Record.Text);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    [Description("Save to long-term memory.")]
    private async Task<string> Remember(
        string? jobId,
        [Description("The text to save. Should be a concise, durable fact or summary.")] string text,
        CancellationToken ct)
    {
        var id = await _store.SaveAsync(jobId, text, ct).ConfigureAwait(false);
        return id is null ? "Error: memory not saved (feature disabled or embedding failed)." : $"Saved memory {id}.";
    }
}
