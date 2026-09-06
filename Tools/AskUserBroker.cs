using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Daggeragent.Tools;

/// <summary>One clickable choice a question offers.</summary>
public sealed record AskUserOption(string Value, string Label, string Description);

/// <summary>One question in an <c>ask_user</c> call. Options are optional — a question with none
/// is a free-text one. Whoever answers can always type something instead of (or as well as)
/// clicking; typed text is carried alongside the choice, never instead of it.</summary>
public sealed record AskUserQuestion(
    string Key, string Header, string Question, IReadOnlyList<AskUserOption> Options, bool MultiSelect);

/// <summary>The agent asking its operator a structured question mid-turn.</summary>
public sealed record AskUserPrompt(
    string RequestId, string JobId, IReadOnlyList<AskUserQuestion> Questions);

/// <summary>What came back for one question.</summary>
public sealed record AskUserAnswer(string Key, IReadOnlyList<string> Chosen, string Text);

/// <summary>
/// A host able to put an <see cref="AskUserPrompt"/> in front of someone and return the answers
/// (null = dismissed/cancelled/timed out). The web SSE stream and the interactive TUI each
/// register one while they are driving a job's turn.
/// </summary>
public interface IAskUserResponder
{
    Task<IReadOnlyList<AskUserAnswer>?> AskAsync(AskUserPrompt prompt, CancellationToken ct);
}

/// <summary>
/// Routes the agent's own <c>ask_user</c> tool call to whoever is driving the job — the web UI's
/// SSE stream (rendered as clickable options in the transcript) or the interactive TUI (numbered
/// options answered on the input line). The same shape as <see cref="PermissionBroker"/>, and
/// deliberately a separate class: a permission decision is a fixed set of allow/deny options with
/// policy fallback, while a question is free-form and its absence of an answerer is itself an
/// answer the model should hear.
///
/// <para>Responders are keyed by job id and registered only while a turn is being driven, so a
/// question with no responder — CLI one-shot, a trigger job, a Banter-mode turn (where the room's
/// own <c>ask_operator</c> is the right primitive), a sub-agent's own job id — returns
/// immediately instead of blocking on a host that isn't there.</para>
/// </summary>
public sealed class AskUserBroker
{
    private readonly ILogger<AskUserBroker> _log;
    private readonly ConcurrentDictionary<string, IAskUserResponder> _responders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IReadOnlyList<AskUserAnswer>?>> _pending =
        new(StringComparer.Ordinal);

    public AskUserBroker(ILogger<AskUserBroker> log) => _log = log;

    /// <summary>Whether anyone is attached who could answer a question for this job.</summary>
    public bool HasResponder(string jobId) => _responders.ContainsKey(jobId);

    /// <summary>Register the responder driving <paramref name="jobId"/>'s current turn. Dispose to detach.</summary>
    public IDisposable RegisterResponder(string jobId, IAskUserResponder responder)
    {
        _responders[jobId] = responder;
        return new Registration(this, jobId, responder);
    }

    /// <summary>
    /// Ask whoever is driving the job. (Handled: false) when nobody is registered; a registered
    /// responder's null means dismissed, cancelled or timed out — a non-answer, not an error.
    /// </summary>
    public async Task<(bool Handled, IReadOnlyList<AskUserAnswer>? Answers)> AskAsync(
        AskUserPrompt prompt, CancellationToken ct)
    {
        if (!_responders.TryGetValue(prompt.JobId, out var responder)) return (false, null);
        try
        {
            return (true, await responder.AskAsync(prompt, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ask_user responder for job {JobId} failed; treating as unanswered", prompt.JobId);
            return (true, null);
        }
    }

    /// <summary>
    /// Park a pending question a push-style responder is waiting on. The prompt goes out on a
    /// one-way channel (an SSE frame) and the answers arrive via <see cref="TryResolve"/> from
    /// a separate HTTP request.
    /// </summary>
    public Task<IReadOnlyList<AskUserAnswer>?> WaitForAnswersAsync(string requestId, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<AskUserAnswer>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;
        var reg = ct.Register(() =>
        {
            if (_pending.TryRemove(requestId, out var pending)) pending.TrySetResult(null);
        });
        return tcs.Task.ContinueWith(t =>
        {
            reg.Dispose();
            return t.Result;
        }, TaskScheduler.Default);
    }

    /// <summary>Deliver answers for a parked question. False if it already resolved or timed out.</summary>
    public bool TryResolve(string requestId, IReadOnlyList<AskUserAnswer>? answers)
    {
        if (!_pending.TryRemove(requestId, out var tcs)) return false;
        tcs.TrySetResult(answers);
        return true;
    }

    private sealed class Registration : IDisposable
    {
        private readonly AskUserBroker _broker;
        private readonly string _jobId;
        private readonly IAskUserResponder _responder;
        public Registration(AskUserBroker broker, string jobId, IAskUserResponder responder)
        {
            _broker = broker;
            _jobId = jobId;
            _responder = responder;
        }
        public void Dispose() =>
            // Remove only our own registration — a nested or replacing registration wins.
            ((ICollection<KeyValuePair<string, IAskUserResponder>>)_broker._responders)
                .Remove(new KeyValuePair<string, IAskUserResponder>(_jobId, _responder));
    }
}
