using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Daggeragent.Llm;

/// <summary>
/// Minimal IChatClient against Anthropic's native Messages API
/// (POST /v1/messages with x-api-key + anthropic-version headers + Anthropic's content-block
/// wire format). Written in-house because Anthropic.SDK's NuGet binaries diverge from the
/// MEAI version we ship — every recent release calls HostedMcpServerTool members that don't
/// exist in MEAI 10.6.0, throwing MissingMethodException the first time tools are passed in.
///
/// Scope: streaming + non-streaming chat with text + tool use + tool results + images. Extended
/// thinking and prompt-caching breakpoints are out of scope (no MEAI surface for them today).
/// </summary>
public sealed class AnthropicChatClient : IChatClient, IDisposable
{
    // Anthropic stamps every request with the API version the wire format was frozen against.
    // 2023-06-01 is the long-stable baseline that supports tool use, streaming, and images.
    private const string AnthropicVersion = "2023-06-01";
    private const int DefaultMaxOutputTokens = 4096;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // Tool results carry arbitrary tool / MCP payloads, not Anthropic wire types, so they get
    // their own neutral serializer (Web defaults; any [JsonPropertyName] on MCP result types
    // still wins) rather than the snake_case wire options above.
    private static readonly JsonSerializerOptions ToolResultJsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string _baseUrl;
    private readonly string _defaultModel;

    public AnthropicChatClient(string apiKey, string defaultModel, string? baseUrl = null, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("apiKey is required", nameof(apiKey));
        _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", AnthropicVersion);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _ownsHttpClient = true;
        _baseUrl = (baseUrl ?? "https://api.anthropic.com").TrimEnd('/');
        _defaultModel = defaultModel;
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType?.IsInstanceOfType(this) == true ? this : null;

    // ─────────────────────────── non-streaming ───────────────────────────

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Build the same body the streaming path uses, just with "stream": false. Anthropic
        // returns the full Message envelope in one shot — easier to reduce into a ChatResponse
        // than to roll the SSE collector twice.
        var body = BuildRequestBody(messages, options, streaming: false);
        using var req = NewPostRequest(body);
        using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        await ThrowIfErrorAsync(response, cancellationToken).ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseFullMessage(doc.RootElement, body.Model);
    }

    // ─────────────────────────── streaming ───────────────────────────

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = BuildRequestBody(messages, options, streaming: true);
        using var req = NewPostRequest(body);
        var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            await ThrowIfErrorAsync(response, cancellationToken).ConfigureAwait(false);

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // Per content_block index, accumulate the in-progress tool_use input json so we can
            // emit a single FunctionCallContent at content_block_stop. (Anthropic streams the
            // input as `partial_json` deltas just like text deltas.) responseId/finishReason
            // travel in a mutable holder because C# iterators can't bind ref/in/out parameters.
            var streamState = new StreamState();

            string? eventName = null;
            var dataSb = new StringBuilder();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;

                if (line.Length == 0)
                {
                    if (dataSb.Length == 0) { eventName = null; continue; }
                    var data = dataSb.ToString();
                    dataSb.Clear();
                    var ev = eventName;
                    eventName = null;

                    foreach (var update in HandleEvent(ev, data, body.Model, streamState))
                        yield return update;
                }
                else if (line.StartsWith("event:", StringComparison.Ordinal))
                    eventName = line.Substring(6).Trim();
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                    dataSb.Append(line.Substring(5).TrimStart());
                // ignore comment lines (":...") and unknown prefixes
            }
        }
        finally
        {
            response.Dispose();
        }
    }

    // ─────────────────────────── event handling ───────────────────────────

    private sealed class StreamState
    {
        public string? ResponseId;
        public string? FinishReason;
        public readonly Dictionary<int, (string CallId, string Name, StringBuilder Json)> ToolInputAcc = new();
    }

    private static IEnumerable<ChatResponseUpdate> HandleEvent(
        string? eventName,
        string data,
        string model,
        StreamState s)
    {
        // JsonDocument is allocated only when we actually care about the event — cheap fast path
        // for ping/keepalive/unknown event names.
        if (eventName is null) yield break;
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;

        switch (eventName)
        {
            case "message_start":
                if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("id", out var id))
                    s.ResponseId = id.GetString();
                break;

            case "content_block_start":
                if (!root.TryGetProperty("index", out var startIdx)) yield break;
                if (!root.TryGetProperty("content_block", out var startBlock)) yield break;
                var startType = startBlock.GetProperty("type").GetString();
                if (startType == "tool_use")
                {
                    var callId = startBlock.GetProperty("id").GetString() ?? "";
                    var name = startBlock.GetProperty("name").GetString() ?? "";
                    s.ToolInputAcc[startIdx.GetInt32()] = (callId, name, new StringBuilder());
                }
                break;

            case "content_block_delta":
                if (!root.TryGetProperty("index", out var deltaIdx)) yield break;
                if (!root.TryGetProperty("delta", out var delta)) yield break;
                var deltaType = delta.GetProperty("type").GetString();
                if (deltaType == "text_delta")
                {
                    var text = delta.GetProperty("text").GetString();
                    if (!string.IsNullOrEmpty(text))
                        yield return new ChatResponseUpdate(ChatRole.Assistant, text) { ResponseId = s.ResponseId, ModelId = model };
                }
                else if (deltaType == "input_json_delta")
                {
                    if (s.ToolInputAcc.TryGetValue(deltaIdx.GetInt32(), out var acc))
                    {
                        var partial = delta.GetProperty("partial_json").GetString() ?? "";
                        acc.Json.Append(partial);
                    }
                }
                else if (deltaType == "thinking_delta")
                {
                    var thinking = delta.GetProperty("thinking").GetString();
                    if (!string.IsNullOrEmpty(thinking))
                    {
                        var u = new ChatResponseUpdate { ResponseId = s.ResponseId, ModelId = model, Role = ChatRole.Assistant };
                        u.Contents.Add(new TextReasoningContent(thinking));
                        yield return u;
                    }
                }
                break;

            case "content_block_stop":
                if (!root.TryGetProperty("index", out var stopIdx)) yield break;
                if (s.ToolInputAcc.TryGetValue(stopIdx.GetInt32(), out var pending))
                {
                    s.ToolInputAcc.Remove(stopIdx.GetInt32());
                    // Parse the accumulated JSON into a dictionary. Missing/invalid → empty args
                    // so the model gets a tool call rather than a stalled turn.
                    IDictionary<string, object?>? args = null;
                    var raw = pending.Json.ToString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        try
                        {
                            using var argDoc = JsonDocument.Parse(raw);
                            args = JsonElementToDictionary(argDoc.RootElement);
                        }
                        catch { args = new Dictionary<string, object?>(); }
                    }
                    var u = new ChatResponseUpdate { ResponseId = s.ResponseId, ModelId = model, Role = ChatRole.Assistant };
                    u.Contents.Add(new FunctionCallContent(pending.CallId, pending.Name, args));
                    yield return u;
                }
                break;

            case "message_delta":
                if (root.TryGetProperty("delta", out var msgDelta) && msgDelta.TryGetProperty("stop_reason", out var reason))
                    s.FinishReason = reason.GetString();
                if (root.TryGetProperty("usage", out var usage))
                {
                    var u = new ChatResponseUpdate { ResponseId = s.ResponseId, ModelId = model };
                    u.Contents.Add(new UsageContent(ParseUsage(usage)));
                    yield return u;
                }
                break;

            case "message_stop":
                if (s.FinishReason != null)
                {
                    var u = new ChatResponseUpdate { ResponseId = s.ResponseId, ModelId = model, FinishReason = MapFinishReason(s.FinishReason) };
                    yield return u;
                }
                break;

            case "error":
                var err = root.TryGetProperty("error", out var errEl) && errEl.TryGetProperty("message", out var errMsg)
                    ? errMsg.GetString() ?? "(unknown anthropic error)"
                    : data;
                throw new InvalidOperationException($"Anthropic API stream error: {err}");

            // ping / unknown — ignore
        }
    }

    private static ChatResponse ParseFullMessage(JsonElement root, string model)
    {
        var contents = new List<AIContent>();
        var assistantMsg = new ChatMessage(ChatRole.Assistant, contents);
        if (root.TryGetProperty("content", out var content))
        {
            foreach (var block in content.EnumerateArray())
            {
                var type = block.GetProperty("type").GetString();
                if (type == "text")
                    contents.Add(new TextContent(block.GetProperty("text").GetString() ?? ""));
                else if (type == "thinking")
                    contents.Add(new TextReasoningContent(block.GetProperty("thinking").GetString() ?? ""));
                else if (type == "tool_use")
                {
                    var callId = block.GetProperty("id").GetString() ?? "";
                    var name = block.GetProperty("name").GetString() ?? "";
                    IDictionary<string, object?>? args = null;
                    if (block.TryGetProperty("input", out var input))
                        args = JsonElementToDictionary(input);
                    contents.Add(new FunctionCallContent(callId, name, args));
                }
            }
        }

        var response = new ChatResponse(assistantMsg) { ModelId = model };
        if (root.TryGetProperty("id", out var id)) response.ResponseId = id.GetString();
        if (root.TryGetProperty("stop_reason", out var reason))
            response.FinishReason = MapFinishReason(reason.GetString());
        if (root.TryGetProperty("usage", out var usage))
            response.Usage = ParseUsage(usage);
        return response;
    }

    private static UsageDetails ParseUsage(JsonElement usage)
    {
        var details = new UsageDetails();
        if (usage.TryGetProperty("input_tokens", out var input)) details.InputTokenCount = input.GetInt64();
        if (usage.TryGetProperty("output_tokens", out var output)) details.OutputTokenCount = output.GetInt64();
        if (details.InputTokenCount is long i && details.OutputTokenCount is long o)
            details.TotalTokenCount = i + o;
        return details;
    }

    private static ChatFinishReason? MapFinishReason(string? reason) => reason switch
    {
        "end_turn" => ChatFinishReason.Stop,
        "max_tokens" => ChatFinishReason.Length,
        "stop_sequence" => ChatFinishReason.Stop,
        "tool_use" => ChatFinishReason.ToolCalls,
        _ => null,
    };

    // ─────────────────────────── request construction ───────────────────────────

    private HttpRequestMessage NewPostRequest(MessagesRequest body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/messages")
        {
            Content = JsonContent.Create(body, options: JsonOpts),
        };
        return req;
    }

    private MessagesRequest BuildRequestBody(IEnumerable<ChatMessage> messages, ChatOptions? options, bool streaming)
    {
        var (systemPrompt, anthMessages) = ConvertMessages(messages);
        var maxTokens = options?.MaxOutputTokens ?? DefaultMaxOutputTokens;
        return new MessagesRequest
        {
            Model = options?.ModelId ?? _defaultModel,
            MaxTokens = maxTokens,
            System = string.IsNullOrEmpty(systemPrompt) ? null : systemPrompt,
            Messages = anthMessages,
            Temperature = options?.Temperature,
            TopP = options?.TopP,
            Tools = ConvertTools(options?.Tools),
            Stream = streaming ? true : null,
        };
    }

    private static (string SystemPrompt, List<MessageDto> Messages) ConvertMessages(IEnumerable<ChatMessage> messages)
    {
        var sys = new StringBuilder();
        var result = new List<MessageDto>();

        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.System)
            {
                var t = msg.Text;
                if (!string.IsNullOrEmpty(t))
                {
                    if (sys.Length > 0) sys.Append("\n\n");
                    sys.Append(t);
                }
                continue;
            }

            var blocks = new List<object>();
            foreach (var c in msg.Contents)
            {
                switch (c)
                {
                    case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                        blocks.Add(new { type = "text", text = tc.Text });
                        break;
                    case FunctionCallContent fc:
                        blocks.Add(new
                        {
                            type = "tool_use",
                            id = fc.CallId,
                            name = fc.Name,
                            input = fc.Arguments ?? new Dictionary<string, object?>(),
                        });
                        break;
                    case FunctionResultContent fr:
                        blocks.Add(new
                        {
                            type = "tool_result",
                            tool_use_id = fr.CallId,
                            content = SerializeToolResult(fr.Result),
                        });
                        break;
                    case DataContent dc when dc.MediaType?.StartsWith("image/") == true:
                        // Anthropic only takes base64 image content blocks today (no URL refs).
                        blocks.Add(new
                        {
                            type = "image",
                            source = new
                            {
                                type = "base64",
                                media_type = dc.MediaType,
                                data = Convert.ToBase64String(dc.Data.Span),
                            },
                        });
                        break;
                }
            }

            if (blocks.Count == 0) continue;

            // Anthropic requires strict user/assistant alternation. Tool results live in the
            // user role (carrying tool_use_id back). We also merge runs of the same role —
            // common when MEAI emits a tool result as its own ChatMessage.
            var role = msg.Role == ChatRole.Tool ? "user" : (msg.Role == ChatRole.Assistant ? "assistant" : "user");
            if (result.Count > 0 && result[^1].Role == role)
            {
                ((List<object>)result[^1].Content).AddRange(blocks);
            }
            else
            {
                result.Add(new MessageDto { Role = role, Content = blocks });
            }
        }

        return (sys.ToString(), result);
    }

    /// <summary>
    /// Render a tool result for the Anthropic <c>tool_result.content</c> field. Mirrors what
    /// MEAI's OpenAI adapter does so the model sees the same payload on either endpoint: a string
    /// passes straight through, a <see cref="JsonElement"/> as its raw JSON, and anything else
    /// (an MCP <c>CallToolResult</c>, a POCO, …) as JSON serialized by its RUNTIME type —
    /// serializing via the <c>object?</c> static type would emit "{}". Falls back to
    /// <c>ToString()</c> if serialization throws so the result is never worse than before.
    /// </summary>
    private static string SerializeToolResult(object? result)
    {
        switch (result)
        {
            case null: return "";
            case string s: return s;
            case JsonElement je: return je.GetRawText();
        }
        try { return JsonSerializer.Serialize(result, result.GetType(), ToolResultJsonOpts); }
        catch { return result.ToString() ?? ""; }
    }

    private static List<ToolDto>? ConvertTools(IList<AITool>? tools)
    {
        if (tools is null || tools.Count == 0) return null;
        var list = new List<ToolDto>(tools.Count);
        foreach (var t in tools)
        {
            if (t is not AIFunction f) continue;
            // f.JsonSchema is a JsonElement describing the full function signature; Anthropic
            // expects the parameter object schema, which is what MEAI's JsonSchema already is.
            list.Add(new ToolDto
            {
                Name = f.Name,
                Description = f.Description,
                InputSchema = f.JsonSchema,
            });
        }
        return list.Count == 0 ? null : list;
    }

    private static IDictionary<string, object?> JsonElementToDictionary(JsonElement el)
    {
        var dict = new Dictionary<string, object?>();
        if (el.ValueKind != JsonValueKind.Object) return dict;
        foreach (var prop in el.EnumerateObject())
            dict[prop.Name] = JsonElementToObject(prop.Value);
        return dict;
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToObject).ToList(),
        JsonValueKind.Object => JsonElementToDictionary(el),
        _ => el.GetRawText(),
    };

    private static async Task ThrowIfErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new InvalidOperationException($"Anthropic API {(int)response.StatusCode}: {body}");
    }

    // ─────────────────────────── DTOs ───────────────────────────

    private sealed class MessagesRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        [JsonPropertyName("system")] public string? System { get; set; }
        [JsonPropertyName("messages")] public List<MessageDto> Messages { get; set; } = new();
        [JsonPropertyName("temperature")] public float? Temperature { get; set; }
        [JsonPropertyName("top_p")] public float? TopP { get; set; }
        [JsonPropertyName("tools")] public List<ToolDto>? Tools { get; set; }
        [JsonPropertyName("stream")] public bool? Stream { get; set; }
    }

    private sealed class MessageDto
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("content")] public object Content { get; set; } = new List<object>();
    }

    private sealed class ToolDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("input_schema")] public JsonElement InputSchema { get; set; }
    }
}
