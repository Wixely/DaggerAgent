using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Daggeragent.Llm;

/// <summary>
/// A per-request record of exactly what went to the model and what came back, for when the logs'
/// one-line summaries are not enough — a delegator that posted tool-call markup into a Banter room,
/// say, where the question is what it was prompted with. Opt-in (<c>Agent:TraceModelIo</c>):
/// prompts carry whatever the conversation does, file contents included.
///
/// <para>Innermost in the pipeline, beneath textual-tool-call salvage and the function loop, so a
/// record is the raw exchange: every message sent (system prompt, history, tool results), the tool
/// names offered, and the reply as the model produced it. One JSON object per line in
/// <c>{TraceDirectory}/{jobId}.jsonl</c>. Binary content is recorded by type and size, not bytes.</para>
/// </summary>
public sealed class ModelTraceChatClient : DelegatingChatClient
{
    private static readonly object FileLock = new();
    private readonly string _path;
    private readonly string _endpointId;
    private readonly ILogger _log;

    public ModelTraceChatClient(IChatClient inner, string directory, string? jobId, string endpointId, ILogger log)
        : base(inner)
    {
        _path = Path.Combine(directory, $"{(string.IsNullOrWhiteSpace(jobId) ? "no-job" : jobId)}.jsonl");
        _endpointId = endpointId;
        _log = log;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sent = messages.ToList();
        var clock = Stopwatch.StartNew();
        try
        {
            var response = await base.GetResponseAsync(sent, options, cancellationToken).ConfigureAwait(false);
            Write(sent, options, response, null, clock.Elapsed, streaming: false);
            return response;
        }
        catch (Exception ex)
        {
            Write(sent, options, null, ex, clock.Elapsed, streaming: false);
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sent = messages.ToList();
        var clock = Stopwatch.StartNew();
        var updates = new List<ChatResponseUpdate>();
        Exception? failure = null;
        try
        {
            await using var e = base.GetStreamingResponseAsync(sent, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await e.MoveNextAsync().ConfigureAwait(false)) break;
                    update = e.Current;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    throw;
                }
                updates.Add(update);
                yield return update;
            }
        }
        finally
        {
            // Also on a cancelled or abandoned stream: what arrived is part of the record.
            Write(sent, options, updates.ToChatResponse(), failure, clock.Elapsed, streaming: true);
        }
    }

    private void Write(
        List<ChatMessage> sent, ChatOptions? options, ChatResponse? response, Exception? failure,
        TimeSpan elapsed, bool streaming)
    {
        try
        {
            var record = new JsonObject
            {
                ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
                ["endpoint"] = _endpointId,
                ["model"] = options?.ModelId ?? response?.ModelId,
                ["streaming"] = streaming,
                ["elapsedMs"] = (long)elapsed.TotalMilliseconds,
                ["tools"] = new JsonArray([.. (options?.Tools ?? []).Select(t => (JsonNode?)JsonValue.Create(t.Name))]),
                ["sent"] = new JsonArray([.. sent.Select(Message)]),
                ["received"] = response is null ? null : new JsonArray([.. response.Messages.Select(Message)]),
                ["finishReason"] = response?.FinishReason?.Value,
                ["usage"] = response?.Usage is { } u
                    ? new JsonObject { ["input"] = u.InputTokenCount, ["output"] = u.OutputTokenCount, ["total"] = u.TotalTokenCount }
                    : null,
                ["error"] = failure is null ? null : $"{failure.GetType().Name}: {failure.Message}",
            };

            var line = record.ToJsonString() + Environment.NewLine;
            lock (FileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, line);
            }
        }
        catch (Exception ex)
        {
            // A trace must never break the turn it is recording.
            _log.LogWarning(ex, "Could not write the model trace to {Path}", _path);
        }
    }

    private static JsonNode Message(ChatMessage m) => new JsonObject
    {
        ["role"] = m.Role.Value,
        ["contents"] = new JsonArray([.. m.Contents.Select(Content)]),
    };

    private static JsonNode? Content(AIContent c) => c switch
    {
        TextReasoningContent r => new JsonObject { ["type"] = "reasoning", ["text"] = r.Text },
        TextContent t => new JsonObject { ["type"] = "text", ["text"] = t.Text },
        FunctionCallContent f => new JsonObject
        {
            ["type"] = "call",
            ["callId"] = f.CallId,
            ["name"] = f.Name,
            ["arguments"] = f.Arguments is null ? null
                : JsonSerializer.SerializeToNode(f.Arguments, AIJsonUtilities.DefaultOptions),
        },
        FunctionResultContent r => new JsonObject
        {
            ["type"] = "result",
            ["callId"] = r.CallId,
            ["result"] = r.Result is string s ? s
                : r.Result is null ? null
                : JsonSerializer.SerializeToNode(r.Result, AIJsonUtilities.DefaultOptions),
        },
        DataContent d => new JsonObject { ["type"] = "data", ["mediaType"] = d.MediaType, ["bytes"] = d.Data.Length },
        UriContent u => new JsonObject { ["type"] = "uri", ["mediaType"] = u.MediaType, ["uri"] = u.Uri.ToString() },
        UsageContent => null,
        _ => new JsonObject { ["type"] = c.GetType().Name },
    };
}
