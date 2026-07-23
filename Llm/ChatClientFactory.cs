using System.ClientModel;
using Daggeragent.Configuration;
using Daggeragent.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OpenAI;

namespace Daggeragent.Llm;

public sealed class ChatClientFactory
{
    private readonly OpenAIOptions _legacy;
    private readonly EndpointsOptions _endpoints;
    private readonly AgentOptions _agentOptions;
    private readonly McpOptions _mcpOptions;
    private readonly ToolsOptions _toolsOptions;
    private readonly HostLaunchInfo _launchInfo;
    private readonly CliSessionStore _cliSessions;
    private readonly ILoggerFactory _loggerFactory;

    public ChatClientFactory(
        IOptions<OpenAIOptions> legacy,
        IOptions<EndpointsOptions> endpoints,
        IOptions<AgentOptions> agentOptions,
        IOptions<McpOptions> mcpOptions,
        IOptions<ToolsOptions> toolsOptions,
        HostLaunchInfo launchInfo,
        CliSessionStore cliSessions,
        ILoggerFactory loggerFactory)
    {
        _legacy = legacy.Value;
        _endpoints = endpoints.Value;
        _agentOptions = agentOptions.Value;
        _mcpOptions = mcpOptions.Value;
        _toolsOptions = toolsOptions.Value;
        _launchInfo = launchInfo;
        _cliSessions = cliSessions;
        _loggerFactory = loggerFactory;
    }

    public IChatClient Create(string? modelOverride = null, string? endpointId = null, string? jobId = null)
        => Create(modelOverride, _agentOptions.MaxTurnsPerInvocation, endpointId, jobId);

    /// <summary>
    /// Build a chat client with an explicit per-request tool-loop cap. Resolves which endpoint
    /// to talk to using, in order: <paramref name="endpointId"/> (per-job override),
    /// <see cref="EndpointsOptions.DefaultId"/>, the first enabled endpoint in <see cref="EndpointsOptions.Items"/>,
    /// or — when <see cref="EndpointsOptions.Items"/> is empty — the legacy <see cref="OpenAIOptions"/>.
    /// <paramref name="jobId"/> is threaded through to CLI-backed endpoints so successive turns
    /// in the same conversation can <c>--resume</c> the spawned CLI's own session.
    /// </summary>
    public IChatClient Create(string? modelOverride, int maxIterations, string? endpointId = null, string? jobId = null)
    {
        var endpoint = ResolveEndpoint(endpointId);
        // Treat an empty/whitespace override the same as unset — fall back to the endpoint's own
        // default. A persisted ConversationState.Model is a non-null string (default ""), so a
        // plain `?? ` would let "" through and hand an empty --model to a CLI or an empty model id
        // to the OpenAI client; both are wrong. Empty here means "let the endpoint/provider decide".
        var model = string.IsNullOrWhiteSpace(modelOverride) ? endpoint.DefaultModel : modelOverride;

        IChatClient baseClient = endpoint.Provider?.Trim().ToLowerInvariant() switch
        {
            "ollama" => new OllamaApiClient(new Uri(string.IsNullOrWhiteSpace(endpoint.BaseUrl) ? "http://localhost:11434" : endpoint.BaseUrl), model),
            // Native Anthropic Messages API.
            "anthropic" => CreateAnthropicNativeClient(endpoint),
            // Local CLI subprocess endpoints — DaggerAgent acts as a UI shell over Claude Code
            // CLI or Codex CLI, reusing the user's existing CLI auth/subscription instead of an
            // API key. The CLI runs autonomously per turn with its own tools.
            "claudecli" or "claude-cli" => CreateCliClient(CliChatClient.CliKind.Claude, endpoint, model, jobId),
            "codexcli" or "codex-cli" => CreateCliClient(CliChatClient.CliKind.Codex, endpoint, model, jobId),
            "copilotcli" or "copilot-cli" => CreateCliClient(CliChatClient.CliKind.Copilot, endpoint, model, jobId),
            // OpenAI-compat path. Covers OpenAI itself, LM Studio, vLLM, OpenRouter, OpenWebUI,
            // and the Anthropic OpenAI-compat shim (just point BaseUrl at https://api.anthropic.com/v1/).
            _ => CreateOpenAiCompatibleClient(endpoint, model),
        };

        return new ChatClientBuilder(baseClient)
            .UseFunctionInvocation(_loggerFactory, fic =>
            {
                fic.MaximumIterationsPerRequest = Math.Max(1, maxIterations);
            })
            .UseLogging(_loggerFactory)
            .Build();
    }

    /// <summary>
    /// Pick the active <see cref="EndpointConfig"/>. Falls back to a synthetic config built
    /// from <see cref="OpenAIOptions"/> so a fresh install with no Endpoints section still works.
    /// </summary>
    private EndpointConfig ResolveEndpoint(string? requestedId)
    {
        // 1. Per-call override
        if (!string.IsNullOrWhiteSpace(requestedId))
        {
            var match = _endpoints.Items.FirstOrDefault(e =>
                string.Equals(e.Id, requestedId, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        // 2. Global default
        if (!string.IsNullOrWhiteSpace(_endpoints.DefaultId))
        {
            var def = _endpoints.Items.FirstOrDefault(e =>
                string.Equals(e.Id, _endpoints.DefaultId, StringComparison.OrdinalIgnoreCase));
            if (def is not null) return def;
        }

        // 3. First enabled item
        var first = _endpoints.Items.FirstOrDefault(e => e.Enabled);
        if (first is not null) return first;

        // 4. Legacy single-endpoint config — synthesise one on the fly so old appsettings.json works.
        return new EndpointConfig
        {
            Id = "legacy",
            DisplayName = "Legacy (from OpenAI section)",
            Provider = _legacy.Provider,
            BaseUrl = _legacy.BaseUrl,
            ApiKey = _legacy.ApiKey,
            DefaultModel = _legacy.DefaultModel,
            RequestTimeoutSeconds = _legacy.RequestTimeoutSeconds,
            Enabled = true,
        };
    }

    private IChatClient CreateAnthropicNativeClient(EndpointConfig endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint.ApiKey))
        {
            throw new InvalidOperationException(
                $"Endpoint '{endpoint.Id}' uses Provider=Anthropic but has no ApiKey. " +
                "Set it in the Endpoints UI or runtime config to a sk-ant-… value.");
        }
        var baseUrl = endpoint.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl) || IsLoopbackUrl(baseUrl))
            baseUrl = "https://api.anthropic.com";
        else
        {
            baseUrl = baseUrl.TrimEnd('/');
            if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                baseUrl = baseUrl[..^3];
        }
        return new AnthropicChatClient(
            apiKey: endpoint.ApiKey,
            defaultModel: endpoint.DefaultModel,
            baseUrl: baseUrl,
            timeout: TimeSpan.FromSeconds(Math.Max(1, endpoint.RequestTimeoutSeconds)));
    }

    private IChatClient CreateCliClient(CliChatClient.CliKind kind, EndpointConfig endpoint, string model, string? jobId)
    {
        // Working dir: the current turn's ambient cwd (ToolExecutionContext, set per request so
        // concurrent CLI-endpoint turns don't share a mutable global) → the sticky ToolsOptions
        // override → the user's shell-launch cwd (NOT the exe content root). The CLI inherits this
        // as its own cwd, so tool calls like reading files resolve against the user's project.
        var configuredCwd = ToolExecutionContext.WorkingDirectory ?? _toolsOptions.WorkingDirectory;
        var cwd = !string.IsNullOrWhiteSpace(configuredCwd)
            ? configuredCwd
            : _launchInfo.OriginalWorkingDirectory;
        var timeout = TimeSpan.FromSeconds(Math.Max(1, endpoint.RequestTimeoutSeconds));
        var binaryPath = kind switch
        {
            CliChatClient.CliKind.Claude => _toolsOptions.ClaudeCliPath,
            CliChatClient.CliKind.Codex => _toolsOptions.CodexCliPath,
            CliChatClient.CliKind.Copilot => _toolsOptions.CopilotCliPath,
            _ => "",
        };
        var permission = new CliChatClient.PermissionFlags(
            ClaudePermissionMode: endpoint.ClaudePermissionMode,
            ClaudeAllowedTools: endpoint.ClaudeAllowedTools,
            ClaudeDangerouslySkipPermissions: endpoint.ClaudeDangerouslySkipPermissions,
            CodexSandbox: endpoint.CodexSandbox,
            CodexAskForApproval: endpoint.CodexAskForApproval,
            CopilotAllowAllTools: endpoint.CopilotAllowAllTools,
            CopilotAllowAllPaths: endpoint.CopilotAllowAllPaths,
            CopilotAllowAllUrls: endpoint.CopilotAllowAllUrls,
            CopilotAutopilot: endpoint.CopilotAutopilot,
            CopilotMaxAutopilotContinues: endpoint.CopilotMaxAutopilotContinues,
            CopilotAllowedTools: endpoint.CopilotAllowedTools,
            CopilotDeniedTools: endpoint.CopilotDeniedTools,
            CopilotNoAskUser: endpoint.CopilotNoAskUser);
        return new CliChatClient(
            kind: kind,
            model: string.IsNullOrWhiteSpace(model) ? null : model,
            jobId: jobId,
            timeout: timeout,
            cwd: cwd,
            mcp: _mcpOptions,
            sessions: _cliSessions,
            logger: _loggerFactory.CreateLogger<CliChatClient>(),
            binaryPathOverride: string.IsNullOrWhiteSpace(binaryPath) ? null : binaryPath,
            permission: permission);
    }

    private IChatClient CreateOpenAiCompatibleClient(EndpointConfig endpoint, string model)
    {
        var apiKey = string.IsNullOrWhiteSpace(endpoint.ApiKey) ? "no-key-required" : endpoint.ApiKey;
        var baseUrl = string.IsNullOrWhiteSpace(endpoint.BaseUrl) ? "http://localhost:1234/v1" : endpoint.BaseUrl;

        var openAi = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(baseUrl),
                NetworkTimeout = TimeSpan.FromSeconds(Math.Max(1, endpoint.RequestTimeoutSeconds)),
            });
        return openAi.GetChatClient(model).AsIChatClient();
    }

    private static bool IsLoopbackUrl(string url) =>
        url.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("127.0.0.1", StringComparison.Ordinal) ||
        url.Contains("::1", StringComparison.Ordinal);
}
