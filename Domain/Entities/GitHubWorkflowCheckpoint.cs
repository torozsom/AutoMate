namespace Domain.Entities;

/// <summary>Stores durable, non-sensitive progress for one GitHub Actions workflow run.</summary>
public sealed class GitHubWorkflowCheckpoint : BaseEntity
{
    public Guid DeploymentId { get; set; }
    public Deployment Deployment { get; set; } = null!;
    public long WorkflowRunId { get; set; }
    public int WorkflowAttempt { get; set; }
    public string? LastWorkflowStateFingerprint { get; set; }
    public DateTimeOffset? FinalReconciledAt { get; set; }
    public ICollection<GitHubWorkflowJobCheckpoint> JobCheckpoints { get; set; } = [];
}
