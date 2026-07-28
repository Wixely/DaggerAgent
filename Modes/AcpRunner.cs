using System.Collections.Concurrent;
using System.Text.Json;
using AgentClientProtocol;
using Daggeragent.Agent;
using Daggeragent.Configuration;
using Daggeragent.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daggeragent.Modes;

/// <summary>
/// ACP (Agent Client Protocol) mode: `dagger acp` speaks JSON-RPC 2.0 over stdin/stdout so
/// editors that host external agents (Zed, the JetBrains IDEs, Neovim/Emacs plugins) can drive
/// DaggerAgent as an embedded coding agent. Sessions map 1:1 onto jobs — the ACP sessionId IS
/// the job id, so a session started in an editor shows up in the web UI and can be resumed
/// later via session/load from the SQLite job store. stdout carries only protocol frames;
/// all logging is routed to stderr/file by Program's mode-aware sink config.
/// </summary>
public sealed class AcpRunner
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AcpRunner> _log;

    public AcpRunner(IServiceProvider services, ILogger<AcpRunner> log)
    {
        _services = services;
        _log = log;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        await _services.GetRequiredService<IJobStore>().InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _services.GetRequiredService<MemoryStore>().InitializeAsync(cancellationToken).ConfigureAwait(false);

        using var connection = new AgentSideConnection(
            client => new DaggerAcpAgent(_services, client, _log),
            Console.In,
            Console.Out);

        _log.LogInformation("ACP agent listening on stdio");
        var readLoop = connection.Open();

        // Exit when the client closes stdin (read loop ends at EOF) or the host shuts down.
        var stopped = new TaskCompletionSource();
        await using var reg = cancellationToken.Register(() => stopped.TrySetResult());
        await Task.WhenAny(readLoop, stopped.Task).ConfigureAwait(false);
        _log.LogInformation("ACP agent shutting down ({Reason})", readLoop.IsCompleted ? "stdin closed" : "host stopping");
        return 0;
    }
}

internal sealed class DaggerAcpAgent : IAcpAgent
{
    private const ushort SupportedProtocolVersion = 1;
    private const int ToolResultExcerptChars = 1024;

    private sealed class AcpSession
    {
        public required ConversationState State { get; init; }
        public string? Cwd { get; set; }
        public CancellationTokenSource? ActiveTurn { get; set; }

        /// <summary>
        /// Cancel the in-flight turn if any. Races with the turn's own completion, which
        /// disposes the CTS — losing that race is fine, the turn is already over.
        /// </summary>
        public void TryCancelActiveTurn()
        {
            try { ActiveTurn?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private readonly IServiceProvider _services;
    private readonly IAcpClient _client;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, AcpSession> _sessions = new(StringComparer.Ordinal);

    public DaggerAcpAgent(IServiceProvider services, IAcpClient client, ILogger log)
    {
        _services = services;
        _client = client;
        _log = log;
    }

    public ValueTask<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken = default)
    {
        _log.LogInformation("ACP initialize: client={Client} v{Version}, requested protocol v{Protocol}",
            request.ClientInfo?.Name ?? "unknown", request.ClientInfo?.Version ?? "?", request.ProtocolVersion);

        return new(new InitializeResponse
        {
            ProtocolVersion = Math.Min(request.ProtocolVersion, SupportedProtocolVersion),
            AgentCapabilities = new AgentCapabilities
            {
                LoadSession = true,
                PromptCapabilities = new PromptCapabilities
                {
                    Image = true,
                    EmbeddedContext = true,
                },
                SessionCapabilities = new SessionCapabilities
                {
                    List = new SessionListCapabilities(),
                    Resume = new SessionResumeCapabilities(),
                    Close = new SessionCloseCapabilities(),
                    Delete = new SessionDeleteCapabilities(),
                },
            },
            AgentInfo = new Implementation
            {
                Name = "dagger",
                Title = "DaggerAgent",
                Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            },
        });
    }

    public ValueTask<AuthenticateResponse> AuthenticateAsync(AuthenticateRequest request, CancellationToken cancellationToken = default)
        => new(new AuthenticateResponse());

    public async ValueTask<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken = default)
    {
        var endpoints = _services.GetRequiredService<IOptions<EndpointsOptions>>().Value;
        var openAi = _services.GetRequiredService<IOptions<OpenAIOptions>>().Value;
        var model = Server.JobsStreamEndpoints.ResolveModel(null, null, endpoints, openAi);

        var agent = _services.GetRequiredService<LlmAgent>();
        var state = agent.CreateState(model);
        state.WorkingDirectory = request.Cwd;
        await _services.GetRequiredService<IJobStore>().SaveAsync(state, cancellationToken).ConfigureAwait(false);

        _sessions[state.Id] = new AcpSession { State = state, Cwd = request.Cwd };

        // The agent's tool surface comes from its own configured MCP servers (McpClientHost);
        // client-supplied servers aren't spun up yet.
        if (request.McpServers.Length > 0)
            _log.LogWarning("ACP client passed {Count} MCP server(s); client-supplied MCP servers are not supported yet and were ignored", request.McpServers.Length);

        _log.LogInformation("ACP session/new: job {JobId} (model={Model}, cwd={Cwd})", state.Id, model, request.Cwd);
        return new NewSessionResponse { SessionId = state.Id, Models = BuildModelState(state) };
    }

    public async ValueTask<LoadSessionResponse> LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken = default)
    {
        var store = _services.GetRequiredService<IJobStore>();
        var state = await store.LoadAsync(request.SessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new AcpException($"No such session: {request.SessionId}", null, -32602);

        state.WorkingDirectory = request.Cwd;
        _sessions[state.Id] = new AcpSession { State = state, Cwd = request.Cwd };

        // Replay the visible transcript so the editor can rebuild its view. Tool calls and
        // system prompts are skipped — the spec only requires the user/agent message stream.
        foreach (var msg in state.History)
        {
            var text = msg.Text;
            if (string.IsNullOrWhiteSpace(text)) continue;
            SessionUpdate? update = null;
            if (msg.Role == ChatRole.User)
                update = new UserMessageChunkSessionUpdate { Content = new TextContentBlock { Text = text } };
            else if (msg.Role == ChatRole.Assistant)
                update = new AgentMessageChunkSessionUpdate { Content = new TextContentBlock { Text = ThinkingSplitter.StripThinking(text) } };
            if (update is null) continue;
            await _client.SessionNotificationAsync(new SessionNotification { SessionId = state.Id, Update = update }, cancellationToken).ConfigureAwait(false);
        }

        _log.LogInformation("ACP session/load: job {JobId} ({Turns} turns replayed)", state.Id, state.TurnsTaken);
        return new LoadSessionResponse { Models = BuildModelState(state) };
    }

    public async ValueTask<PromptResponse> PromptAsync(PromptRequest request, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(request.SessionId, out var session))
            throw new AcpException($"No such session: {request.SessionId}", null, -32602);

        var (prompt, attachments) = ConvertPrompt(request.Prompt);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new AcpException("Prompt contained no text content", null, -32602);

        var agent = _services.GetRequiredService<LlmAgent>();
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        session.ActiveTurn = turnCts;
        var seenToolCalls = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            using (Tools.ToolExecutionContext.Use(session.Cwd))
            {
                await foreach (var update in agent.RunStreamingTurnAsync(session.State, prompt, attachments, turnCts.Token).ConfigureAwait(false))
                {
                    foreach (var content in update.Contents)
                    {
                        var mapped = MapContent(content, seenToolCalls);
                        if (mapped is null) continue;
                        await _client.SessionNotificationAsync(
                            new SessionNotification { SessionId = request.SessionId, Update = mapped },
                            turnCts.Token).ConfigureAwait(false);
                    }
                }
            }

            return new PromptResponse
            {
                StopReason = session.State.LastTurnFinishReason == "length" ? StopReason.MaxTokens : StopReason.EndTurn,
            };
        }
        catch (OperationCanceledException)
        {
            // LlmAgent's finally block has already closed out the turn and persisted state.
            return new PromptResponse { StopReason = StopReason.Cancelled };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ACP prompt turn failed for session {SessionId}", request.SessionId);
            throw new AcpException(ex.Message, null, -32603);
        }
        finally
        {
            session.ActiveTurn = null;
        }
    }

    public ValueTask CancelAsync(CancelNotification notification, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(notification.SessionId, out var session))
        {
            _log.LogInformation("ACP session/cancel: job {JobId}", notification.SessionId);
            session.TryCancelActiveTurn();
        }
        return default;
    }

    public async ValueTask<ListSessionsResponse> ListSessionsAsync(ListSessionsRequest request, CancellationToken cancellationToken = default)
    {
        var records = await _services.GetRequiredService<IJobStore>().ListAsync(100, cancellationToken).ConfigureAwait(false);
        var sessions = new List<SessionInfo>();
        foreach (var record in records)
        {
            if (!string.IsNullOrEmpty(record.ParentId)) continue; // sub-agent jobs aren't editor sessions
            var (cwd, title) = ExtractSessionSummary(record.StateJson);
            if (!string.IsNullOrWhiteSpace(request.Cwd) && !string.Equals(cwd, request.Cwd, StringComparison.OrdinalIgnoreCase))
                continue;
            sessions.Add(new SessionInfo
            {
                SessionId = record.Id,
                Cwd = cwd ?? "",
                Title = title,
                UpdatedAt = record.UpdatedAt.ToString("O"),
            });
        }
        return new ListSessionsResponse { Sessions = sessions.ToArray() };
    }

    public async ValueTask<ResumeSessionResponse> ResumeSessionAsync(ResumeSessionRequest request, CancellationToken cancellationToken = default)
    {
        // Unlike session/load, resume doesn't replay the transcript — the client kept its own view.
        var state = await _services.GetRequiredService<IJobStore>().LoadAsync(request.SessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new AcpException($"No such session: {request.SessionId}", null, -32602);

        state.WorkingDirectory = request.Cwd;
        _sessions[state.Id] = new AcpSession { State = state, Cwd = request.Cwd };
        _log.LogInformation("ACP session/resume: job {JobId}", state.Id);
        return new ResumeSessionResponse { Models = BuildModelState(state) };
    }

    public ValueTask<CloseSessionResponse> CloseSessionAsync(CloseSessionRequest request, CancellationToken cancellationToken = default)
    {
        // Drop the in-memory session only — the job stays persisted and listable.
        if (_sessions.TryRemove(request.SessionId, out var session))
            session.TryCancelActiveTurn();
        _log.LogInformation("ACP session/close: job {JobId}", request.SessionId);
        return new(new CloseSessionResponse());
    }

    public async ValueTask<DeleteSessionResponse> DeleteSessionAsync(DeleteSessionRequest request, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryRemove(request.SessionId, out var session))
            session.TryCancelActiveTurn();
        await _services.GetRequiredService<IJobStore>().DeleteAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        _log.LogInformation("ACP session/delete: job {JobId}", request.SessionId);
        return new DeleteSessionResponse();
    }

    public ValueTask<ForkSessionResponse> ForkSessionAsync(ForkSessionRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException(); // session/fork is unstable spec; surfaces as method-not-found

    public ValueTask<SetSessionModeResponse> SetSessionModeAsync(SetSessionModeRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException(); // surfaces as JSON-RPC method-not-found

    public async ValueTask<SetSessionModelResponse> SetSessionModelAsync(SetSessionModelRequest request, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(request.SessionId, out var session))
            throw new AcpException($"No such session: {request.SessionId}", null, -32602);

        var endpoints = _services.GetRequiredService<IOptions<EndpointsOptions>>().Value;
        var openAi = _services.GetRequiredService<IOptions<OpenAIOptions>>().Value;
        var ep = endpoints.Items.FirstOrDefault(e => string.Equals(e.Id, request.ModelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new AcpException($"No such model/endpoint: {request.ModelId}", null, -32602);

        session.State.EndpointId = ep.Id;
        session.State.Model = Server.JobsStreamEndpoints.ResolveModel(null, ep.Id, endpoints, openAi);
        await _services.GetRequiredService<IJobStore>().SaveAsync(session.State, cancellationToken).ConfigureAwait(false);
        _log.LogInformation("ACP session/set_model: job {JobId} -> endpoint {EndpointId} (model={Model})", session.State.Id, ep.Id, session.State.Model);
        return new SetSessionModelResponse();
    }

    /// <summary>
    /// ACP has one model-picker concept; DaggerAgent has endpoints (each with a default model).
    /// Advertise each enabled endpoint as a selectable "model" so editors expose the same choice
    /// the web UI's endpoint dropdown does. Null when no endpoints are configured (legacy
    /// OpenAI-only config) — the picker just doesn't appear.
    /// </summary>
    private SessionModelState? BuildModelState(ConversationState state)
    {
        var endpoints = _services.GetRequiredService<IOptions<EndpointsOptions>>().Value;
        var enabled = endpoints.Items.Where(e => e.Enabled).ToList();
        if (enabled.Count == 0) return null;

        var current = !string.IsNullOrWhiteSpace(state.EndpointId) ? state.EndpointId
            : !string.IsNullOrWhiteSpace(endpoints.DefaultId) ? endpoints.DefaultId
            : enabled[0].Id;
        return new SessionModelState
        {
            CurrentModelId = current,
            AvailableModels = enabled.Select(e => new ModelInfo
            {
                ModelId = e.Id,
                Name = string.IsNullOrWhiteSpace(e.DefaultModel) ? e.Id : $"{e.Id} ({e.DefaultModel})",
                Description = e.Provider,
            }).ToArray(),
        };
    }

    public ValueTask<JsonElement> ExtMethodAsync(string method, JsonElement request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public ValueTask ExtNotificationAsync(string method, JsonElement notification, CancellationToken cancellationToken = default)
        => default;

    /// <summary>
    /// Flatten the ACP content blocks into the text prompt LlmAgent expects, carrying images
    /// through as <see cref="DataContent"/> attachments (same shape the web UI sends). Embedded
    /// resources are inlined fenced by their URI; resource links degrade to a mention the model
    /// can chase with its own read tools.
    /// </summary>
    private static (string prompt, IReadOnlyList<AIContent>? attachments) ConvertPrompt(ContentBlock[] blocks)
    {
        var text = new System.Text.StringBuilder();
        List<AIContent>? attachments = null;

        foreach (var block in blocks)
        {
            switch (block)
            {
                case TextContentBlock t:
                    text.Append(t.Text);
                    break;
                case ResourceContentBlock r when r.Resource is TextResourceContents tr:
                    text.AppendLine();
                    text.AppendLine($"```{tr.Uri}");
                    text.AppendLine(tr.Text);
                    text.AppendLine("```");
                    break;
                case ResourceLinkContentBlock link:
                    text.Append(link.Uri);
                    break;
                case ImageContentBlock img:
                    try
                    {
                        (attachments ??= []).Add(new DataContent(Convert.FromBase64String(img.Data), img.MimeType));
                    }
                    catch (FormatException) { /* malformed base64 — drop the image, keep the turn */ }
                    break;
            }
        }
        return (text.ToString(), attachments);
    }

    private static SessionUpdate? MapContent(AIContent content, HashSet<string> seenToolCalls)
    {
        switch (content)
        {
            case TextReasoningContent rc when !string.IsNullOrEmpty(rc.Text):
                return new AgentThoughtChunkSessionUpdate { Content = new TextContentBlock { Text = rc.Text } };

            case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                return new AgentMessageChunkSessionUpdate { Content = new TextContentBlock { Text = tc.Text } };

            case FunctionCallContent fc:
                // Some providers re-emit the call alongside its result; only announce it once.
                if (!string.IsNullOrEmpty(fc.CallId) && !seenToolCalls.Add(fc.CallId)) return null;
                return new ToolCallSessionUpdate
                {
                    ToolCallId = fc.CallId ?? Guid.NewGuid().ToString("N"),
                    Title = fc.Name,
                    Kind = MapToolKind(fc.Name),
                    Status = ToolCallStatus.InProgress,
                    RawInput = TrySerialize(fc.Arguments),
                };

            case FunctionResultContent fr:
                var resultText = fr.Result switch
                {
                    null => "",
                    string s => s,
                    JsonElement je => je.ToString(),
                    var o => o.ToString() ?? "",
                };
                var excerpt = resultText.Length > ToolResultExcerptChars
                    ? resultText[..ToolResultExcerptChars] + "…(truncated)"
                    : resultText;
                return new ToolCallUpdateSessionUpdate
                {
                    ToolCallId = fr.CallId ?? "",
                    Status = ToolCallStatus.Completed,
                    Content = [new ContentToolCallContent { Content = new TextContentBlock { Text = excerpt } }],
                };

            default:
                return null;
        }
    }

    /// <summary>Best-effort mapping of DaggerAgent tool names onto ACP's tool-kind taxonomy so editors pick sensible icons.</summary>
    private static ToolKind MapToolKind(string name) => name switch
    {
        _ when name.Contains("grep", StringComparison.OrdinalIgnoreCase)
            || name.Contains("search", StringComparison.OrdinalIgnoreCase)
            || name.Contains("find", StringComparison.OrdinalIgnoreCase) => ToolKind.Search,
        _ when name.Contains("read", StringComparison.OrdinalIgnoreCase)
            || name.Contains("list", StringComparison.OrdinalIgnoreCase)
            || name.Contains("head", StringComparison.OrdinalIgnoreCase) => ToolKind.Read,
        _ when name.Contains("write", StringComparison.OrdinalIgnoreCase)
            || name.Contains("edit", StringComparison.OrdinalIgnoreCase)
            || name.Contains("patch", StringComparison.OrdinalIgnoreCase) => ToolKind.Edit,
        _ when name.Contains("delete", StringComparison.OrdinalIgnoreCase)
            || name.Contains("remove", StringComparison.OrdinalIgnoreCase) => ToolKind.Delete,
        _ when name.Contains("move", StringComparison.OrdinalIgnoreCase)
            || name.Contains("rename", StringComparison.OrdinalIgnoreCase) => ToolKind.Move,
        _ when name.Contains("shell", StringComparison.OrdinalIgnoreCase)
            || name.Contains("exec", StringComparison.OrdinalIgnoreCase) => ToolKind.Execute,
        _ when name.Contains("http", StringComparison.OrdinalIgnoreCase)
            || name.Contains("web", StringComparison.OrdinalIgnoreCase)
            || name.Contains("fetch", StringComparison.OrdinalIgnoreCase) => ToolKind.Fetch,
        _ when name.Contains("plan", StringComparison.OrdinalIgnoreCase) => ToolKind.Think,
        _ => ToolKind.Other,
    };

    private static JsonElement? TrySerialize(object? value)
    {
        if (value is null) return null;
        try { return JsonSerializer.SerializeToElement(value); }
        catch { return null; }
    }

    /// <summary>
    /// Pull the working directory and a display title (the first user message) out of a
    /// persisted job's state JSON without deserializing the whole ConversationState —
    /// session/list touches up to 100 rows and only needs these two fields.
    /// </summary>
    private static (string? cwd, string? title) ExtractSessionSummary(string stateJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(stateJson);
            var root = doc.RootElement;
            var cwd = root.TryGetProperty("WorkingDirectory", out var wd) ? wd.GetString() : null;
            string? title = null;
            if (root.TryGetProperty("History", out var history) && history.ValueKind == JsonValueKind.Array)
            {
                foreach (var msg in history.EnumerateArray())
                {
                    if (!msg.TryGetProperty("Role", out var role) || role.GetString() != "user") continue;
                    if (msg.TryGetProperty("Contents", out var contents) && contents.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var content in contents.EnumerateArray())
                        {
                            if (content.TryGetProperty("$type", out var type) && type.GetString() == "text" &&
                                content.TryGetProperty("Text", out var text))
                            {
                                title = text.GetString();
                                break;
                            }
                        }
                    }
                    break;
                }
            }
            if (title is { Length: > 80 }) title = title[..80] + "…";
            return (cwd, title);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
