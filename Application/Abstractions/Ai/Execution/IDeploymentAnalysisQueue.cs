namespace Application.Abstractions.Ai;

/// <summary>
///     Detached claim metadata; the lease token fences this attempt, and no diagnostic context
///     is queued.
/// </summary>
/// <param name="AnalysisId">Persisted analysis identity.</param>
/// <param name="DeploymentId">The exact deployment identity used for ownership or correlation.</param>
/// <param name="LeaseId">Unique ownership generation; never reused on recovery.</param>
/// <param name="LeaseUntil">Initial lease expiry; successful renewals may extend it.</param>
/// <param name="Attempt">Acquisitions since the last scheduled retry, including interruptions.</param>
/// <param name="ProviderRetryCount">
///     Durable scheduled transient retries, independent of interrupted acquisitions.
/// </param>
public sealed record DeploymentAnalysisWorkItem(
    Guid AnalysisId,
    Guid DeploymentId,
    Guid LeaseId,
    DateTimeOffset LeaseUntil,
    int Attempt,
    int ProviderRetryCount = 0);

/// <summary>
///     Atomic durable lease boundary; analysis admission persists work in the same transaction.
/// </summary>
public interface IDeploymentAnalysisQueue
{
    /// <summary>
    ///     Claims one eligible queued/recoverable analysis atomically; excludes expired and
    ///     terminal results.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning the owned claim, or null when no eligible work is available.</returns>
    Task<DeploymentAnalysisWorkItem?> ClaimNextAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Extends only a current unexpired owned lease; false means this worker must stop.
    /// </summary>
    /// <param name="work">The detached analysis claim and its lease ownership token.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning true only when this worker still owns the renewed, unexpired lease.</returns>
    Task<bool> RenewAsync(DeploymentAnalysisWorkItem work, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Releases only a current unexpired owned lease after interruption; completed/replaced
    ///     leases are untouched.
    /// </summary>
    /// <param name="work">The detached analysis claim and its lease ownership token.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning true only when the current owned lease was released.</returns>
    Task<bool> ReleaseAsync(DeploymentAnalysisWorkItem work, CancellationToken cancellationToken = default);
}