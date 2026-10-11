namespace Application.Abstractions.Ai;

/// <summary>Detached claim metadata; the lease token fences this attempt, and no diagnostic context is queued.</summary>
/// <param name="AnalysisId">Persisted analysis identity.</param>
/// <param name="DeploymentId">Actual deployment scope resolved from metadata.</param>
/// <param name="LeaseId">Unique ownership generation; never reused on recovery.</param>
/// <param name="LeaseUntil">Initial lease expiry; successful renewals may extend it.</param>
/// <param name="Attempt">Acquisitions since the last scheduled retry, including interruptions.</param>
/// <param name="ProviderRetryCount">Durable scheduled transient retries, independent of interrupted acquisitions.</param>
public sealed record DeploymentAnalysisWorkItem(
    Guid AnalysisId,
    Guid DeploymentId,
    Guid LeaseId,
    DateTimeOffset LeaseUntil,
    int Attempt,
    int ProviderRetryCount = 0);

/// <summary>Atomic durable lease boundary; analysis admission persists work in the same transaction.</summary>
public interface IDeploymentAnalysisQueue
{
    /// <summary>Claims one eligible queued/recoverable analysis atomically; excludes expired and terminal results.</summary>
    Task<DeploymentAnalysisWorkItem?> ClaimNextAsync(CancellationToken cancellationToken = default);

    /// <summary>Extends only a current unexpired owned lease; false means this worker must stop.</summary>
    Task<bool> RenewAsync(DeploymentAnalysisWorkItem work, CancellationToken cancellationToken = default);

    /// <summary>Releases only a current unexpired owned lease after interruption; completed/replaced leases are untouched.</summary>
    Task<bool> ReleaseAsync(DeploymentAnalysisWorkItem work, CancellationToken cancellationToken = default);
}