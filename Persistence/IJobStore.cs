using Daggeragent.Agent;

namespace Daggeragent.Persistence;

public interface IJobStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ConversationState state, CancellationToken cancellationToken = default);
    Task<ConversationState?> LoadAsync(string jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRecord>> ListAsync(int limit = 50, CancellationToken cancellationToken = default);
    Task DeleteAsync(string jobId, CancellationToken cancellationToken = default);
    Task AppendEventAsync(string jobId, string kind, string payloadJson, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flip every job with <see cref="Agent.JobStatus.Running"/> back to
    /// <see cref="Agent.JobStatus.Paused"/> and stamp <see cref="Agent.ConversationState.Interrupted"/>=true.
    /// Called once at process start to reconcile rows orphaned by an ungraceful shutdown.
    /// Returns the ids of jobs that were swept (caller decides whether to auto-resume).
    /// </summary>
    Task<IReadOnlyList<string>> SweepOrphansAsync(CancellationToken cancellationToken = default);
}
