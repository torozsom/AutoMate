namespace Domain.Entities;

/// <summary>Stores durable, non-sensitive progress for one GitHub Actions workflow run.</summary>
public sealed class GitHubWorkflowCheckpoint : BaseEntity
{
    /// <summary>Deployment that owns this workflow run.</summary>
    public Guid DeploymentId { get; set; }

    /// <summary>Related deployment entity.</summary>
    public Deployment Deployment { get; set; } = null!;

    /// <summary>GitHub workflow run identifier.</summary>
    public long WorkflowRunId { get; set; }

    /// <summary>Attempt number for a rerun of the workflow.</summary>
    public int WorkflowAttempt { get; set; }

    /// <summary>Digest of the last published workflow state.</summary>
    public string? LastWorkflowStateFingerprint { get; set; }

    /// <summary>Time the completed-run archive reconciliation finished.</summary>
    public DateTimeOffset? FinalReconciledAt { get; set; }

    /// <summary>Per-job output cursors for this workflow attempt.</summary>
    public ICollection<GitHubWorkflowJobCheckpoint> JobCheckpoints { get; set; } = [];
}