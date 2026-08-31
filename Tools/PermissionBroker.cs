using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Daggeragent.Tools;

/// <summary>One choice a permission prompt offers. Kind uses ACP's vocabulary
/// (<c>allow_once</c> / <c>allow_always</c> / <c>reject_once</c> / <c>reject_always</c>) as the
/// lingua franca; other protocols' decisions are mapped onto it.</summary>
public sealed record PermissionPromptOption(string Id, string Name, string Kind);

/// <summary>A delegated agent asking for permission, normalised across protocols.</summary>
public sealed record PermissionPrompt(
    string RequestId,
    string JobId,
    string AgentName,
    string Title,
    IReadOnlyList<PermissionPromptOption> Options);

/// <summary>
/// A host able to put a <see cref="PermissionPrompt"/> in front of someone and return the chosen
/// option id (null = dismissed/cancelled). The web SSE stream and the agent-side ACP connection
/// each register one while they are driving a job's turn.
/// </summary>
public interface IPermissionResponder
{
    Task<string?> AskAsync(PermissionPrompt prompt, CancellationToken ct);
}

/// <summary>
/// Routes a delegated agent's permission request up to whoever is driving the job — the web UI's
/// SSE stream, or the editor above an agent-side ACP session. This is the upstream half of the
/// proxy shape: DaggerAgent sits between a host and a delegated agent, and the delegated agent's
/// "may I run this?" travels through to the host instead of being answered from standing policy.
///
/// <para>Responders are keyed by job id and registered only while a turn is being driven, so a
/// prompt with no responder (CLI mode, a fire-and-forget trigger job, a sub-agent's own job id)
/// falls back to the caller's standing policy — never blocks waiting for a host that isn't
/// there. Decisions for push-style hosts (SSE) come back through <see cref="TryResolve"/>.</para>
/// </summary>
public sealed class PermissionBroker
{
    private readonly ILogger<PermissionBroker> _log;
    private readonly ConcurrentDictionary<string, IPermissionResponder> _responders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _pending = new(StringComparer.Ordinal);

    public PermissionBroker(ILogger<PermissionBroker> log) => _log = log;

    /// <summary>Register the responder driving <paramref name="jobId"/>'s current turn. Dispose to detach.</summary>
    public IDisposable RegisterResponder(string jobId, IPermissionResponder responder)
    {
        _responders[jobId] = responder;
        return new Registration(this, jobId, responder);
    }

    /// <summary>
    /// Ask whoever is driving the job. Returns (handled: false) when no responder is registered —
    /// the caller then applies its standing policy. A registered responder's null answer means
    /// the human dismissed or the wait was cancelled, which is a decision (treat as deny), not
    /// an absence of one.
    /// </summary>
    public async Task<(bool Handled, string? OptionId)> AskAsync(PermissionPrompt prompt, CancellationToken ct)
    {
        if (!_responders.TryGetValue(prompt.JobId, out var responder)) return (false, null);
        try
        {
            var choice = await responder.AskAsync(prompt, ct).ConfigureAwait(false);
            return (true, choice);
        }
        catch (OperationCanceledException)
        {
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Permission responder for job {JobId} failed; treating as unhandled", prompt.JobId);
            return (false, null);
        }
    }

    /// <summary>
    /// Park a pending decision a push-style responder is waiting on. The prompt goes out on a
    /// one-way channel (an SSE frame) and the answer arrives via <see cref="TryResolve"/> from
    /// a separate HTTP request.
    /// </summary>
    public Task<string?> WaitForDecisionAsync(string requestId, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
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

    /// <summary>Deliver a decision for a parked prompt. False if it already resolved or timed out.</summary>
    public bool TryResolve(string requestId, string? optionId)
    {
        if (!_pending.TryRemove(requestId, out var tcs)) return false;
        tcs.TrySetResult(optionId);
        return true;
    }

    private sealed class Registration : IDisposable
    {
        private readonly PermissionBroker _broker;
        private readonly string _jobId;
        private readonly IPermissionResponder _responder;
        public Registration(PermissionBroker broker, string jobId, IPermissionResponder responder)
        {
            _broker = broker;
            _jobId = jobId;
            _responder = responder;
        }
        public void Dispose() =>
            // Remove only our own registration — a nested or replacing registration wins.
            ((ICollection<KeyValuePair<string, IPermissionResponder>>)_broker._responders)
                .Remove(new KeyValuePair<string, IPermissionResponder>(_jobId, _responder));
    }
}