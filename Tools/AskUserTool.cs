using System.Text.Json;
using Daggeragent.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daggeragent.Tools;

/// <summary>
/// The built-in <c>ask_user</c> tool: ask whoever is driving this job a structured question —
/// options they click, free text they can always type instead — and wait for the answer.
/// Neither the OpenAI nor the Anthropic API has a native primitive for this, so it is an
/// ordinary tool-loop entry, the same shape Claude Code's <c>AskUserQuestion</c>, Codex's
/// <c>ask_user_question</c> and Banter's <c>ask_operator</c> use (and the same argument
/// vocabulary as Banter's, so a model that learned one can drive the other).
///
/// <para>Registered for every agent; whether it can actually reach a human is decided per turn
/// by whether a responder is attached to the job — the web transcript and the interactive TUI
/// attach one while they drive a turn. With nobody attached the tool answers immediately and
/// says so, which keeps it harmless in CLI one-shots, trigger jobs and Banter rooms (where the
/// room's own <c>ask_operator</c> is the right way to ask).</para>
/// </summary>
public sealed class AskUserTool
{
    private const string SchemaJson = """
        {"type":"object","properties":{
          "questions":{"type":"array","minItems":1,"maxItems":4,"items":{
            "type":"object","properties":{
              "key":{"type":"string","description":"Short identifier for this question, so you can tell the answers apart."},
              "header":{"type":"string","description":"Two or three words naming the decision, e.g. 'Deploy window'."},
              "question":{"type":"string","description":"What you need decided, and why it is needed."},
              "options":{"type":"array","items":{
                "type":"object","properties":{
                  "value":{"type":"string","description":"What comes back if this is chosen."},
                  "label":{"type":"string","description":"What the person reads."},
                  "description":{"type":"string","description":"Why they would pick it."}
                },"required":["value","label"]}},
              "multi_select":{"type":"boolean","description":"Whether several options may be chosen at once."}
            },"required":["key","question"]}}
        },"required":["questions"]}
        """;

    private readonly AskUserBroker _broker;
    private readonly ToolsOptions _options;
    private readonly ILogger<AskUserTool> _log;

    public AskUserTool(AskUserBroker broker, IOptions<ToolsOptions> options, ILogger<AskUserTool> log)
    {
        _broker = broker;
        _options = options.Value;
        _log = log;
    }

    public AITool Build(string? jobId) => new AskUserFunction(this, jobId ?? "");

    private async Task<string> InvokeAsync(string jobId, JsonElement args, CancellationToken cancellationToken)
    {
        if (!_broker.HasResponder(jobId))
        {
            return "Nobody is attached to this job's UI, so there is no one to ask. If a room or " +
                   "channel ask tool (such as ask_operator) is available to you, use that instead; " +
                   "otherwise proceed with your best judgment and state clearly what you assumed.";
        }

        IReadOnlyList<AskUserQuestion> questions;
        try
        {
            questions = ParseQuestions(args);
        }
        catch (InvalidOperationException ex)
        {
            return $"error: {ex.Message}";
        }

        var prompt = new AskUserPrompt(Guid.NewGuid().ToString("N"), jobId, questions);
        var timeout = TimeSpan.FromSeconds(Math.Max(10, _options.AskUserTimeoutSeconds));

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);

        bool timedOut;
        IReadOnlyList<AskUserAnswer>? answers;
        try
        {
            var (handled, got) = await _broker.AskAsync(prompt, bounded.Token).ConfigureAwait(false);
            answers = handled ? got : null;
            timedOut = answers is null && bounded.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            answers = null;
            timedOut = true;
        }

        if (answers is { Count: > 0 })
        {
            _log.LogInformation("ask_user answered for job {JobId} ({Count} answer(s))", jobId, answers.Count);
            return DescribeAnswers(questions, answers);
        }

        return timedOut
            ? $"Nobody answered within {timeout.TotalMinutes:F0} minutes. Do not assume an answer: " +
              "say that you are still waiting, and stop rather than guessing at what was not decided."
            : "The question was dismissed without an answer. Do not assume one.";
    }

    /// <summary>Typed text outranks what was clicked: they wrote it because the options did not
    /// say what they meant. Chosen values are reported by their labels where they have one.</summary>
    private static string DescribeAnswers(IReadOnlyList<AskUserQuestion> questions, IReadOnlyList<AskUserAnswer> answers)
    {
        var said = answers.Select(a =>
        {
            var q = questions.FirstOrDefault(x => x.Key == a.Key);
            var chosen = a.Chosen
                .Select(v => q?.Options.FirstOrDefault(o => o.Value == v)?.Label ?? v)
                .ToList();

            var value = chosen.Count > 0 ? string.Join(", ", chosen) : "";
            if (a.Text.Length > 0)
            {
                value = value.Length > 0 ? $"{value} (and said: {a.Text})" : a.Text;
            }

            return $"{a.Key} = {(value.Length > 0 ? value : "nothing chosen")}";
        });

        return $"The user answered: {string.Join("; ", said)}";
    }

    private static IReadOnlyList<AskUserQuestion> ParseQuestions(JsonElement args)
    {
        if (!args.TryGetProperty("questions", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("'questions' is required and must be an array.");
        }

        var parsed = list.EnumerateArray().Select(q => new AskUserQuestion(
            Key: Text(q, "key"),
            Header: Optional(q, "header") is { Length: > 0 } h ? h : Text(q, "key"),
            Question: Text(q, "question"),
            Options: q.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array
                ? [.. options.EnumerateArray().Select(o => new AskUserOption(
                    Text(o, "value"), Text(o, "label"), Optional(o, "description") ?? ""))]
                : [],
            MultiSelect: q.TryGetProperty("multi_select", out var multi) && multi.ValueKind == JsonValueKind.True))
            .ToList();

        return parsed.Count == 0
            ? throw new InvalidOperationException("Ask at least one question.")
            : parsed;
    }

    private static string? Optional(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Text(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : throw new InvalidOperationException($"'{name}' is required and must be a string.");

    private sealed class AskUserFunction : AIFunction
    {
        private static readonly JsonElement Schema = ParseSchema();
        private readonly AskUserTool _owner;
        private readonly string _jobId;

        public AskUserFunction(AskUserTool owner, string jobId)
        {
            _owner = owner;
            _jobId = jobId;
        }

        public override string Name => "ask_user";
        public override string Description =>
            "Ask the person driving this session a question and WAIT for the answer, when something " +
            "needs a human decision - spending money, choosing between approaches, anything outside " +
            "what you were asked to do. Offer options where there are sensible ones and they are " +
            "shown as buttons to click; the person can always type something else instead. Returns " +
            "what they chose. Ask more than one question at once only when they are really one " +
            "decision in parts.";
        public override JsonElement JsonSchema => Schema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            using var doc = JsonDocument.Parse(
                JsonSerializer.Serialize<IDictionary<string, object?>>(arguments));
            return await _owner.InvokeAsync(_jobId, doc.RootElement, cancellationToken).ConfigureAwait(false);
        }

        private static JsonElement ParseSchema()
        {
            using var doc = JsonDocument.Parse(SchemaJson);
            return doc.RootElement.Clone();
        }
    }
}
