using Domain.Enums;

namespace Domain.Entities;

/// <summary>A durable SaaS control record without provider access tokens.</summary>
public sealed class CloudDeploymentRun : BaseEntity
{
    /// <summary>AutoMate account charged for admission.</summary>
    public Guid UserId { get; set; }
    /// <summary>Authorized application being deployed.</summary>
    public Guid ProjectId { get; set; }
    /// <summary>Deployment row created when launch begins.</summary>
    public Guid? DeploymentId { get; set; }
    /// <summary>Stable key supplied by a client for safe request retries.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;
    /// <summary>GitHub App installation authorized for the repository.</summary>
    public long InstallationId { get; set; }
    /// <summary>Numeric GitHub repository identity used for webhook correlation.</summary>
    public long RepositoryId { get; set; }
    /// <summary>Repository owner at admission time.</summary>
    public string RepositoryOwner { get; set; } = string.Empty;
    /// <summary>Repository name at admission time.</summary>
    public string RepositoryName { get; set; } = string.Empty;
    /// <summary>Managed deployment branch.</summary>
    public string BranchName { get; set; } = string.Empty;
    /// <summary>Deployment environment used for serialized delivery.</summary>
    public string EnvironmentName { get; set; } = string.Empty;
    /// <summary>Workflow file used for run matching.</summary>
    public string WorkflowFileName { get; set; } = string.Empty;
    /// <summary>Customer ACR login server.</summary>
    public string RegistryServer { get; set; } = string.Empty;
    /// <summary>Protected JSON snapshot of non-token inputs; custom environment values are encrypted.</summary>
    public string? SnapshotJson { get; set; }
    /// <summary>Current execution phase.</summary>
    public CloudRunPhase Phase { get; set; } = CloudRunPhase.Queued;
    /// <summary>Commit that triggered GitHub Actions.</summary>
    public string? CommitSha { get; set; }
    /// <summary>Matched GitHub Actions run.</summary>
    public long? WorkflowRunId { get; set; }
    /// <summary>Number of launch attempts.</summary>
    public int Attempt { get; set; }
    /// <summary>Earliest next claim time after a transient failure.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }
    /// <summary>Worker owning the current lease.</summary>
    public Guid? LeaseOwner { get; set; }
    /// <summary>Time after which a crashed worker's claim may be recovered.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }
    /// <summary>Time this request first entered launch processing.</summary>
    public DateTimeOffset? LaunchStartedAt { get; set; }
    /// <summary>Time AutoMate committed the deployment files.</summary>
    public DateTimeOffset? CommittedAt { get; set; }
    /// <summary>Time the remote workflow reached a terminal conclusion.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Safe, bounded failure reason for the UI.</summary>
    public string? FailureReason { get; set; }
}
