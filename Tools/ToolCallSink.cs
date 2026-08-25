using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Daggeragent.Tools;

/// <summary>
/// One tool invocation, as seen from outside the agent. Raised twice per call — once when the
/// tool starts and once when it finishes — so a host can render "running tool: grep" and then
/// clear it.
///
/// <para><see cref="JobId"/> and <see cref="Depth"/> are both present because sub-agents run as
/// separate jobs: <c>spawn_subagent</c> children are their own job at depth 1, and without both
/// an embedder cannot tell which conversation a tool call belongs to.</para>
///
/// <para>Raw arguments are deliberately absent. Tool arguments routinely carry file contents,
/// paths and credentials, and this event is built to be displayed. <see cref="ArgsDigest"/>
/// carries the argument <em>names</em> plus a short hash of the values — enough to show what was
/// called and to spot the same call repeating, without putting the values themselves on screen.</para>
/// </summary>
public sealed record ToolCallEvent(string JobId, int Depth, string ToolName, string ArgsDigest)
{
    /// <summary>Wall-clock duration. Zero on the started event.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>Null on the started event; true/false on completion.</summary>
    public bool? Succeeded { get; init; }

    /// <summary>Length of the result when it is a string, else 0. Null on the started event.</summary>
    public int? ResultChars { get; init; }

    /// <summary>
    /// Exception type name when the call threw. The message is deliberately excluded — it can
    /// quote the arguments this event is careful not to carry.
    /// </summary>
    public string? Error { get; init; }
}

/// <summary>
/// Where <see cref="NotifyingAIFunction"/> publishes. Registered as a singleton: LlmAgent is
/// transient and SubAgentManager resolves its own from a child scope, so a per-agent event would
/// silently miss exactly the sub-agent activity <see cref="ToolCallEvent.Depth"/> exists to
/// attribute.
/// </summary>
public interface IToolCallSink
{
    void Started(ToolCallEvent e);
    void Completed(ToolCallEvent e);
}

/// <summary>
/// Default in-process sink. Subscribe to the events to drive a status display.
///
/// A subscriber that throws must not fail the tool call it is merely observing, so every
/// invocation is isolated and logged.
/// </summary>
public sealed class ToolCallSink : IToolCallSink
{
    private readonly ILogger<ToolCallSink> _log;

    public ToolCallSink(ILogger<ToolCallSink> log) => _log = log;

    public event Action<ToolCallEvent>? ToolCallStarted;
    public event Action<ToolCallEvent>? ToolCallCompleted;

    public void Started(ToolCallEvent e) => Raise(ToolCallStarted, e, nameof(ToolCallStarted));
    public void Completed(ToolCallEvent e) => Raise(ToolCallCompleted, e, nameof(ToolCallCompleted));

    private void Raise(Action<ToolCallEvent>? handlers, ToolCallEvent e, string which)
    {
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList().Cast<Action<ToolCallEvent>>())
        {
            try { h(e); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "A {Which} subscriber threw for tool {Tool} (job {JobId})",
                    which, e.ToolName, e.JobId);
            }
        }
    }

    /// <summary>
    /// Argument names in sorted order plus a short hash of the serialised values — e.g.
    /// <c>path,limit#3f2a9c11</c>. Names are useful for display; values never leave this method.
    /// Identical calls produce an identical digest, which is what makes a repeating tool visible.
    /// </summary>
    public static string DigestArgs(AIFunctionArguments? arguments)
    {
        if (arguments is null) return "(none)";
        var ordered = arguments.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        if (ordered.Count == 0) return "(none)";

        var names = string.Join(",", ordered.Select(kv => kv.Key));
        string hash;
        try
        {
            var json = JsonSerializer.Serialize(ordered.ToDictionary(kv => kv.Key, kv => kv.Value));
            hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..8].ToLowerInvariant();
        }
        catch
        {
            // A value that won't serialise tells us nothing worth crashing a tool call over.
            return names;
        }
        return $"{names}#{hash}";
    }
}
