namespace Daggeragent.Tools;

/// <summary>
/// Shared helpers for reading a child process's pipes when the run may be killed.
///
/// The rule every caller here follows: start <c>ReadToEndAsync()</c> WITHOUT a cancellation
/// token. On timeout the kill closes the pipes, which ends those reads naturally with whatever
/// the child had written. Passing the token instead races the kill — the reader can cancel
/// before the pipes drain, and <c>ReadToEndAsync</c> throws away its whole buffer when it does,
/// so you wait out the full timeout and get nothing back.
/// </summary>
internal static class ProcessOutput
{
    /// <summary>Default bounded wait for the pipes to drain once a kill has been issued.</summary>
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Bounded await over an in-flight ReadToEndAsync task — used on the timeout path so we can
    /// grab whatever the child managed to write before the kill propagated, without hanging
    /// forever if a pipe didn't actually close. Returns "" if it doesn't drain in time.
    /// </summary>
    public static async Task<string> ReadPartialAsync(Task<string> readTask, TimeSpan timeout)
    {
        try
        {
            var done = await Task.WhenAny(readTask, Task.Delay(timeout)).ConfigureAwait(false);
            if (done == readTask) return await readTask.ConfigureAwait(false);
        }
        catch { /* swallow — caller treats an unreadable pipe as no output */ }
        return "";
    }

    /// <inheritdoc cref="ReadPartialAsync(Task{string}, TimeSpan)"/>
    public static Task<string> ReadPartialAsync(Task<string> readTask) =>
        ReadPartialAsync(readTask, DrainTimeout);
}
