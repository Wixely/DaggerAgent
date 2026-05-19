using System.Text;
using Daggeragent.Configuration;
using Daggeragent.Llm;
using Daggeragent.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daggeragent.Agent;

public sealed class ContextCompressor
{
    /// <summary>
    /// Prefix tagging synthetic summary system messages so subsequent compressions can fold
    /// them into the next summary instead of accumulating one per compression cycle.
    /// </summary>
    public const string SummaryMarker = "[summary of earlier conversation]";

    private readonly ChatClientFactory _chatClientFactory;
    private readonly TokenEstimator _tokenEstimator;
    private readonly MemoryStore _memoryStore;
    private readonly AgentOptions _options;
    private readonly ILogger<ContextCompressor> _log;

    public ContextCompressor(ChatClientFactory chatClientFactory, TokenEstimator tokenEstimator, MemoryStore memoryStore, IOptions<AgentOptions> options, ILogger<ContextCompressor> log)
    {
        _chatClientFactory = chatClientFactory;
        _tokenEstimator = tokenEstimator;
        _memoryStore = memoryStore;
        _options = options.Value;
        _log = log;
    }

    public async Task CompressAsync(ConversationState state, CancellationToken cancellationToken = default)
    {
        var history = state.History;
        if (history.Count < _options.CompressionKeepLastTurns * 2 + 2) return;

        var keep = _options.CompressionKeepLastTurns * 2;

        // Split: preserved system messages (default prompt, dagger.md, anything else durable)
        // vs prior summary system messages (foldable). Prior summaries get re-summarised with
        // the new prefix so we end up with exactly one [summary ...] message after every
        // compression, regardless of how many cycles have run.
        var preservedSystem = new List<ChatMessage>();
        var priorSummaries = new List<ChatMessage>();
        var nonSystem = new List<ChatMessage>();

        foreach (var m in history)
        {
            if (m.Role == ChatRole.System)
            {
                if (m.Text?.StartsWith(SummaryMarker, StringComparison.Ordinal) == true)
                    priorSummaries.Add(m);
                else
                    preservedSystem.Add(m);
            }
            else
            {
                nonSystem.Add(m);
            }
        }

        if (nonSystem.Count <= keep && priorSummaries.Count <= 1) return;

        var toSummarise = nonSystem.Take(Math.Max(0, nonSystem.Count - keep)).ToList();
        var tail = nonSystem.Skip(Math.Max(0, nonSystem.Count - keep)).ToList();

        var summaryPrompt = BuildSummaryPrompt(priorSummaries, toSummarise);
        // Empty SummariserModel means "use the same model the agent is using" — ChatClientFactory.Create(null)
        // falls back to the OpenAI.DefaultModel.
        var summariser = _chatClientFactory.Create(string.IsNullOrWhiteSpace(_options.SummariserModel) ? null : _options.SummariserModel);

        var summaryMessages = new List<ChatMessage>
        {
            new(ChatRole.System,
                "You compress conversation history. You may be given a PRIOR_SUMMARY (the previous compression's output) " +
                "and NEW_MESSAGES (turns that occurred since). Produce ONE consolidated summary that preserves facts, " +
                "decisions, file paths, identifiers, tool results and open questions. Do not invent. Aim for the shortest " +
                "form that loses no actionable detail."),
            new(ChatRole.User, summaryPrompt),
        };

        var response = await summariser.GetResponseAsync(summaryMessages, options: null, cancellationToken).ConfigureAwait(false);
        var summaryText = response.Text ?? "";

        var compressedHistory = new List<ChatMessage>();
        compressedHistory.AddRange(preservedSystem);
        compressedHistory.Add(new ChatMessage(ChatRole.System, $"{SummaryMarker}\n{summaryText}"));
        compressedHistory.AddRange(tail);

        state.History = compressedHistory;
        state.ApproxTokenCount = _tokenEstimator.Estimate(state.History);
        _log.LogInformation(
            "Compressed history for job {JobId}: {OldCount} -> {NewCount} messages " +
            "(folded {PriorSummaryCount} prior summary, summarised {NewlySummarisedCount} new msgs), approx tokens {Tokens}",
            state.Id, history.Count, state.History.Count, priorSummaries.Count, toSummarise.Count, state.ApproxTokenCount);

        // Cross-session memory: persist this summary so future jobs can recall it. Best-effort —
        // failures (embedding unreachable, feature disabled) are logged but don't fail compression.
        if (_memoryStore.Enabled && !string.IsNullOrWhiteSpace(summaryText))
        {
            await _memoryStore.SaveAsync(state.Id, summaryText, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string BuildSummaryPrompt(IList<ChatMessage> priorSummaries, IList<ChatMessage> newMessages)
    {
        var sb = new StringBuilder();

        if (priorSummaries.Count > 0)
        {
            sb.AppendLine("=== PRIOR_SUMMARY ===");
            foreach (var s in priorSummaries)
            {
                // Strip the marker line so the model isn't confused by the framing tag.
                var text = s.Text ?? "";
                if (text.StartsWith(SummaryMarker, StringComparison.Ordinal))
                    text = text.Substring(SummaryMarker.Length).TrimStart('\r', '\n');
                sb.AppendLine(text);
            }
            sb.AppendLine();
        }

        sb.AppendLine("=== NEW_MESSAGES ===");
        foreach (var msg in newMessages)
        {
            sb.Append('[').Append(msg.Role.Value).Append("] ");
            if (!string.IsNullOrEmpty(msg.Text)) sb.AppendLine(msg.Text);
            foreach (var c in msg.Contents)
            {
                if (c is FunctionCallContent fc)
                    sb.Append("  (tool-call ").Append(fc.Name).Append(' ').AppendLine(System.Text.Json.JsonSerializer.Serialize(fc.Arguments));
                else if (c is FunctionResultContent fr)
                    sb.Append("  (tool-result ").Append(fr.CallId).Append("): ").AppendLine(fr.Result?.ToString());
            }
        }

        sb.AppendLine();
        sb.AppendLine("Produce one consolidated summary covering everything above.");
        return sb.ToString();
    }
}
