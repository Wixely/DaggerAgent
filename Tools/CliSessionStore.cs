using System.Collections.Concurrent;

namespace Daggeragent.Tools;

/// <summary>
/// In-memory map of (jobId, cli-kind) → external CLI session id + the cwd that produced it.
/// Lets successive calls to <c>delegate_to_claude</c> / <c>delegate_to_codex</c> in the same
/// DaggerAgent job pass <c>--resume &lt;session&gt;</c> so the spawned CLI continues its own
/// conversation instead of starting cold each time.
///
/// The cwd tag matters because Claude scopes sessions to the project directory: a session id
/// captured in cwd A returns "No conversation found with session ID …" when handed to a Claude
/// run from cwd B. <see cref="Get"/> only returns the session when the requested cwd matches
/// what was stored — cross-dir continues fall back to a fresh session automatically.
///
/// Not persisted — Dagger restarts wipe this, after which the next call falls back to a fresh
/// session (model agnostic; harmless).
/// </summary>
public sealed class CliSessionStore
{
    private readonly ConcurrentDictionary<(string JobId, string Cli), Entry> _map = new();

    public string? Get(string jobId, string cli, string cwd) =>
        _map.TryGetValue((jobId, cli), out var e)
            && string.Equals(e.Cwd, cwd, StringComparison.OrdinalIgnoreCase)
                ? e.SessionId
                : null;

    /// <summary>
    /// Diagnostic-only: returns the cwd a stashed session was created in, if any. Lets the
    /// caller log a "dropping session because cwd changed from X to Y" message without
    /// re-keying the dictionary first.
    /// </summary>
    public string? GetStoredCwd(string jobId, string cli) =>
        _map.TryGetValue((jobId, cli), out var e) ? e.Cwd : null;

    public void Set(string jobId, string cli, string cwd, string sessionId) =>
        _map[(jobId, cli)] = new Entry(sessionId, cwd);

    public bool Clear(string jobId, string cli) =>
        _map.TryRemove((jobId, cli), out _);

    private readonly record struct Entry(string SessionId, string Cwd);
}
