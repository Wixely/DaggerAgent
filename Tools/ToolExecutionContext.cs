namespace Daggeragent.Tools;

/// <summary>
/// Ambient, per-turn context for the built-in tools. Carries the working directory of the CURRENT
/// turn through the async flow so the singleton tools (<see cref="FilesystemTools"/>,
/// <see cref="ShellToolset"/>, …) can resolve a per-job cwd <em>without</em> mutating the shared
/// <c>ToolsOptions</c> singleton — which two concurrent turns (a UI job + a triggered job, or
/// several triggered jobs) would clobber, since the filesystem-jail root is derived from that
/// value.
///
/// Set via <see cref="Use"/> at a turn's entry point. Because it's an <see cref="AsyncLocal{T}"/>,
/// each request/turn — and any sub-agent turn or fire-and-forget triggered job spawned within it —
/// carries its own value, and concurrent flows don't see each other's. A null (unset) value means
/// "fall back to <c>ToolsOptions.WorkingDirectory</c> / the launch cwd", preserving prior behaviour
/// for the paths (CLI, interactive, triggers) that don't set it.
/// </summary>
public static class ToolExecutionContext
{
    private static readonly AsyncLocal<string?> _workingDirectory = new();

    /// <summary>The current turn's working-directory override, or null to use the configured default.</summary>
    public static string? WorkingDirectory => _workingDirectory.Value;

    /// <summary>
    /// Set the working directory for the enclosing async scope. Empty/whitespace is treated as
    /// "no override" (null). Disposing restores the previous value, so nested sub-agent turns and
    /// concurrent requests can't leak into each other.
    /// </summary>
    public static IDisposable Use(string? workingDirectory)
    {
        var previous = _workingDirectory.Value;
        _workingDirectory.Value = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;
        public Scope(string? previous) => _previous = previous;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _workingDirectory.Value = _previous;
        }
    }
}
