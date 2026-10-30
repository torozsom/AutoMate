namespace Domain.Entities.Ai;

/// <summary>
///     Durable metadata-only work item with fenced lease ownership for a requested analysis.
/// </summary>
public sealed class DeploymentAnalysisWorkItem : BaseEntity
{
    /// <summary>One persisted analysis per work item.</summary>
    public Guid AnalysisId { get; init; }

    /// <summary>Analysis metadata and safe result; never a diagnostic snapshot.</summary>
    public AiDeploymentAnalysis Analysis { get; init; } = null!;

    /// <summary>Latest acquisition time; retained for legacy compatibility and operator inspection.</summary>
    public DateTimeOffset? ClaimedAt { get; init; }

    /// <summary>Unique ownership generation, replaced on each acquisition.</summary>
    public Guid? LeaseId { get; init; }

    /// <summary>Deadline after which another worker can reclaim unfinished work.</summary>
    public DateTimeOffset? LeaseUntil { get; init; }

    /// <summary>
    ///     Acquisitions since the latest scheduled retry; the recovery limit stops repeated
    ///     interrupted processing.
    /// </summary>
    public int AttemptCount { get; init; }

    /// <summary>
    ///     Durable number of scheduled transient retries, independent of interruption acquisitions.
    /// </summary>
    public int ProviderRetryCount { get; init; }

    /// <summary>Earliest acquisition time after a transient failure; null means immediately eligible.</summary>
    public DateTimeOffset? NextAttemptAt { get; init; }

    /// <summary>Terminal completion time, committed atomically with the analysis result.</summary>
    public DateTimeOffset? CompletedAt { get; init; }
}
