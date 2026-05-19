using System.CommandLine;
using System.Text.Json;
using Daggeragent.Agent;
using Daggeragent.Configuration;
using Daggeragent.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daggeragent.Modes;

public sealed class CliRunner
{
    private readonly IServiceProvider _services;
    private readonly ILogger<CliRunner> _log;

    public CliRunner(IServiceProvider services, ILogger<CliRunner> log)
    {
        _services = services;
        _log = log;
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var promptOption = new Option<string?>("--prompt", "-p") { Description = "Prompt to send to the agent. If omitted, reads from stdin or remaining positional args." };
        var resumeOption = new Option<string?>("--resume") { Description = "Resume a previous job by id." };
        var modelOption = new Option<string?>("--model", "-m") { Description = "Model override." };
        var systemOption = new Option<string?>("--system", "-s") { Description = "System prompt override (new jobs only)." };
        var jsonOption = new Option<bool>("--json") { Description = "Emit machine-readable JSON output." };
        var promptArgs = new Argument<string[]>("words") { Description = "Positional words concatenated as the prompt.", Arity = ArgumentArity.ZeroOrMore };

        var root = new RootCommand("DaggerAgent CLI");
        var runCmd = new Command("run", "Run a one-shot agent task")
        {
            promptOption, resumeOption, modelOption, systemOption, jsonOption, promptArgs,
        };
        root.Subcommands.Add(runCmd);

        // Allow bare invocation `dagger.exe "do this"` (no `run` subcommand) and stdin-only
        // invocation (no args) — both re-route to the `run` subcommand.
        if (args.Length == 0 || !args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
        {
            var rerouted = new List<string> { "run" };
            rerouted.AddRange(args);
            args = rerouted.ToArray();
        }

        var parsed = root.Parse(args);
        if (parsed.Errors.Count > 0)
        {
            foreach (var err in parsed.Errors) Console.Error.WriteLine(err.Message);
            return 1;
        }

        if (parsed.CommandResult.Command != runCmd)
        {
            Console.Error.WriteLine("usage: Dagger run [--prompt <text> | positional] [--resume <id>] [--model <name>] [--system <text>] [--json]");
            return 1;
        }

        var prompt = parsed.GetValue(promptOption);
        var resume = parsed.GetValue(resumeOption);
        var model = parsed.GetValue(modelOption);
        var system = parsed.GetValue(systemOption);
        var json = parsed.GetValue(jsonOption);
        var positional = parsed.GetValue(promptArgs) ?? Array.Empty<string>();

        if (string.IsNullOrEmpty(prompt) && positional.Length > 0)
        {
            prompt = string.Join(' ', positional);
        }
        if (string.IsNullOrEmpty(prompt) && Console.IsInputRedirected)
        {
            prompt = await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(prompt))
        {
            Console.Error.WriteLine("error: no prompt provided.");
            return 1;
        }

        var agent = _services.GetRequiredService<LlmAgent>();
        var store = _services.GetRequiredService<IJobStore>();
        var openAi = _services.GetRequiredService<IOptions<OpenAIOptions>>().Value;
        var agentOpts = _services.GetRequiredService<IOptions<AgentOptions>>().Value;

        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _services.GetRequiredService<Persistence.MemoryStore>().InitializeAsync(cancellationToken).ConfigureAwait(false);

        ConversationState state;
        if (!string.IsNullOrWhiteSpace(resume))
        {
            var loaded = await store.LoadAsync(resume, cancellationToken).ConfigureAwait(false);
            if (loaded is null)
            {
                Console.Error.WriteLine($"error: no such job: {resume}");
                return 1;
            }
            state = loaded;
        }
        else
        {
            var chosenModel = model ?? openAi.DefaultModel;
            state = agent.CreateState(chosenModel, system);
        }

        try
        {
            var response = await agent.RunTurnAsync(state, prompt!, cancellationToken).ConfigureAwait(false);
            // Strip inline <think>...</think> from the visible reply so piping (`dagger.exe "..." > out.txt`)
            // captures just the answer. TextReasoningContent (the OpenAI/LM-Studio reasoning channel) is
            // already excluded from response.Text by MEAI.
            var text = Agent.ThinkingSplitter.StripThinking(response.Text ?? "");
            if (json)
            {
                var payload = new
                {
                    jobId = state.Id,
                    status = state.Status.ToString(),
                    model = state.Model,
                    text,
                    turnsTaken = state.TurnsTaken,
                    approxTokenCount = state.ApproxTokenCount,
                    totalInputTokens = state.TotalInputTokens,
                    totalOutputTokens = state.TotalOutputTokens,
                    totalThinkingTokens = state.TotalThinkingTokens,
                    totalCostUsd = state.TotalCostUsd,
                };
                Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false }));
            }
            else
            {
                Console.WriteLine(text);
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "CLI turn failed");
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }
}
