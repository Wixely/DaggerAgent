using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Banter.Agents.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Daggeragent.Llm;

/// <summary>
/// Turns a tool call the model WROTE into the tool call it meant. Small and local models do this
/// constantly — a chat template without tool support, an endpoint that does not implement
/// function calling, a model trained on the Hermes format — and the reply is
/// <c>&lt;tool_call&gt;{"name":"read_file",…}&lt;/tool_call&gt;</c> while nothing runs. In a Banter
/// room that markup was posted as the agent's answer.
///
/// <para>Sits beneath the function-invocation loop, so a salvaged call is run exactly like a real
/// one — any tool in the request (DaggerAgent's registry or a Banter room's), result fed back,
/// model continues — in the same turn, without a corrective re-prompt. Recognition is the Banter
/// SDK's <see cref="TextualToolCall"/> (Hermes <c>&lt;tool_call&gt;</c>, <c>&lt;function_call&gt;</c>,
/// Llama <c>&lt;function=…&gt;</c>, bare or fenced JSON), and only a reply that is ONE call and
/// nothing else, naming a tool the request actually offered, is converted: a model that wrote a
/// sentence meant it to be read.</para>
///
/// <para>Streaming holds the text back while what has arrived could still be a call, and releases
/// it unchanged the moment it cannot — prose streams as before, after at most a few characters.
/// A leading <c>&lt;think&gt;…&lt;/think&gt;</c> block is looked past, since reasoning models put
/// one before the call.</para>
/// </summary>
public sealed class TextualToolCallChatClient : DelegatingChatClient
{
    private readonly ILogger _log;

    public TextualToolCallChatClient(IChatClient inner, ILogger log) : base(inner) => _log = log;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        if (OfferedTools(options) is not { Count: > 0 } offered)
        {
            return response;
        }

        var last = response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
        if (last is null || last.Contents.OfType<FunctionCallContent>().Any())
        {
            return response;
        }

        var text = string.Concat(last.Contents.OfType<TextContent>().Select(t => t.Text));
        if (TrySalvage(text, offered) is not { } call)
        {
            return response;
        }

        last.Contents = [.. last.Contents.Where(c => c is not TextContent), call];
        response.FinishReason = ChatFinishReason.ToolCalls;
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var offered = OfferedTools(options);
        if (offered is not { Count: > 0 })
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
            yield break;
        }

        var held = new List<ChatResponseUpdate>();
        var allText = new StringBuilder();   // every text character of the reply so far
        var released = 0;                    // how many of those have already gone downstream
        var holding = true;

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (!holding)
            {
                yield return update;
                continue;
            }

            // A real tool call means the model is using the protocol; nothing to salvage.
            if (update.Contents.OfType<FunctionCallContent>().Any())
            {
                holding = false;
                foreach (var h in held) yield return h;
                held.Clear();
                yield return update;
                continue;
            }

            var updateText = string.Concat(update.Contents.OfType<TextContent>().Select(t => t.Text));
            allText.Append(updateText);
            var all = allText.ToString();

            // "<t" could open a think block or a <tool_call>; wait until it says which.
            if (IsThinkPrefix(all))
            {
                held.Add(update);
                continue;
            }

            var visibleStart = VisibleStart(all, out var thinkOpen);

            // Reasoning streams as it always did: a think block is never a call, so it is not held.
            if (thinkOpen)
            {
                foreach (var h in held) yield return h;
                held.Clear();
                yield return update;
                released = all.Length;
                continue;
            }

            if (visibleStart > released)
            {
                // The think block closed in this update (or what was held turned out to open one):
                // release up to the end of it, and hold only what follows.
                foreach (var h in held) yield return h;
                held.Clear();
                var cut = Math.Max(0, visibleStart - (all.Length - updateText.Length));
                if (cut >= updateText.Length)
                {
                    yield return update;
                }
                else
                {
                    yield return WithText(update, updateText[..cut], keepOthers: true);
                    held.Add(WithText(update, updateText[cut..], keepOthers: false));
                }
                released = visibleStart;
            }
            else
            {
                held.Add(update);
            }

            if (!TextualToolCall.MightBe(all[visibleStart..]))
            {
                holding = false;
                foreach (var h in held) yield return h;
                held.Clear();
            }
        }

        var heldText = allText;

        if (!holding || held.Count == 0)
        {
            yield break;
        }

        if (TrySalvage(heldText.ToString(), offered) is { } call)
        {
            // Everything but the text survives — reasoning, usage, the response id — and the
            // text is replaced by the call it spelled out.
            foreach (var h in held)
            {
                var rest = h.Contents.Where(c => c is not TextContent).ToList();
                if (rest.Count > 0)
                {
                    yield return new ChatResponseUpdate(h.Role ?? ChatRole.Assistant, rest)
                    {
                        ResponseId = h.ResponseId,
                        MessageId = h.MessageId,
                        ModelId = h.ModelId,
                    };
                }
            }

            var last = held[^1];
            yield return new ChatResponseUpdate(ChatRole.Assistant, [call])
            {
                ResponseId = last.ResponseId,
                MessageId = last.MessageId,
                ModelId = last.ModelId,
                FinishReason = ChatFinishReason.ToolCalls,
            };
            yield break;
        }

        foreach (var h in held) yield return h;
    }

    /// <summary>The names of the function tools this request offered, as offered.</summary>
    private static IReadOnlyDictionary<string, string>? OfferedTools(ChatOptions? options) =>
        options?.Tools?.OfType<AIFunctionDeclaration>()
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);

    private FunctionCallContent? TrySalvage(string text, IReadOnlyDictionary<string, string> offered)
    {
        var start = VisibleStart(text, out var thinkOpen);
        if (thinkOpen || IsThinkPrefix(text) || !TextualToolCall.TryParse(text[start..], out var name, out var arguments))
        {
            return null;
        }

        if (!offered.TryGetValue(name, out var exact))
        {
            _log.LogWarning("Model wrote a call to '{Tool}', which this request did not offer — left as text", name);
            return null;
        }

        Dictionary<string, object?>? args;
        try
        {
            args = JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments);
        }
        catch (JsonException)
        {
            return null;
        }

        _log.LogInformation("Salvaged a tool call the model wrote as text: {Tool}", exact);
        return new FunctionCallContent($"textual_{Guid.NewGuid():N}", exact, args ?? []);
    }

    private const string ThinkOpen = "<think>", ThinkClose = "</think>";

    /// <summary>Whether the text so far is the start of <c>&lt;think&gt;</c> and nothing more yet.</summary>
    private static bool IsThinkPrefix(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.Length > 0 && trimmed.Length < ThinkOpen.Length
            && ThinkOpen.StartsWith(trimmed, StringComparison.Ordinal);
    }

    /// <summary>Where the reply proper begins: after a leading <c>&lt;think&gt;…&lt;/think&gt;</c>
    /// block if there is one, else at 0. <paramref name="open"/> is true while that block has
    /// started and not yet closed.</summary>
    private static int VisibleStart(string text, out bool open)
    {
        open = false;
        var lead = text.Length - text.TrimStart().Length;
        if (string.CompareOrdinal(text, lead, ThinkOpen, 0, ThinkOpen.Length) != 0)
        {
            return 0;
        }

        var close = text.IndexOf(ThinkClose, lead + ThinkOpen.Length, StringComparison.Ordinal);
        if (close < 0)
        {
            open = true;
            return text.Length;
        }
        return close + ThinkClose.Length;
    }

    /// <summary>A copy of <paramref name="update"/> carrying <paramref name="text"/> as its text,
    /// and its other contents only when <paramref name="keepOthers"/>. The finish reason goes with
    /// the later half.</summary>
    private static ChatResponseUpdate WithText(ChatResponseUpdate update, string text, bool keepOthers)
    {
        var contents = keepOthers ? update.Contents.Where(c => c is not TextContent).ToList() : [];
        if (text.Length > 0)
        {
            contents.Add(new TextContent(text));
        }

        return new ChatResponseUpdate(update.Role ?? ChatRole.Assistant, contents)
        {
            ResponseId = update.ResponseId,
            MessageId = update.MessageId,
            ModelId = update.ModelId,
            CreatedAt = update.CreatedAt,
            FinishReason = keepOthers ? null : update.FinishReason,
        };
    }
}
