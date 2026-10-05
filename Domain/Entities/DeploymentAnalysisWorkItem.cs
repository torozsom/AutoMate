namespace Domain.Entities;

/// <summary>Durable metadata-only work item with fenced lease ownership for a requested analysis.</summary>
public sealed class DeploymentAnalysisWorkItem : BaseEntity
{
    /// <summary>One persisted analysis per work item.</summary>
    public Guid AnalysisId { get; set; }

    /// <summary>Analysis metadata and safe result; never a diagnostic snapshot.</summary>
    public AiDeploymentAnalysis Analysis { get; set; } = null!;

    /// <summary>Latest acquisition time; retained for legacy compatibility and operator inspection.</summary>
    public DateTimeOffset? ClaimedAt { get; set; }

    /// <summary>Unique ownership generation, replaced on each acquisition.</summary>
    public Guid? LeaseId { get; set; }

    /// <summary>Deadline after which another worker can reclaim unfinished work.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>Acquisitions since the latest scheduled retry; the recovery limit stops repeated interrupted processing.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Durable number of scheduled transient retries, independent of interruption acquisitions.</summary>
    public int ProviderRetryCount { get; set; }

    /// <summary>Earliest acquisition time after a transient failure; null means immediately eligible.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>Terminal completion time, committed atomically with the analysis result.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
}