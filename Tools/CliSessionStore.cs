using System.Collections.Concurrent;

namespace Daggeragent.Tools;

/// <summary>
/// In-memory map of (jobId, cli-kind) → external CLI session id. Lets successive calls
/// to <c>delegate_to_claude</c> / <c>delegate_to_codex</c> in the same DaggerAgent job
/// pass <c>--resume &lt;session&gt;</c> so the spawned CLI continues its own conversation
/// instead of starting cold each time. Not persisted — Dagger restarts wipe this, after
/// which the next call falls back to a fresh session (model agnostic; harmless).
/// </summary>
public sealed class CliSessionStore
{
    private readonly ConcurrentDictionary<(string JobId, string Cli), string> _map = new();

    public string? Get(string jobId, string cli) =>
        _map.TryGetValue((jobId, cli), out var sid) ? sid : null;

    public void Set(string jobId, string cli, string sessionId) =>
        _map[(jobId, cli)] = sessionId;

    public bool Clear(string jobId, string cli) =>
        _map.TryRemove((jobId, cli), out _);
}
