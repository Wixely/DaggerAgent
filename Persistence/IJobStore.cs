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
}
