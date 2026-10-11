using Domain.Enums;

namespace Domain.Entities;

/// <summary>Stores a redacted-log checkpoint and state fingerprint for one GitHub Actions job.</summary>
public sealed class GitHubWorkflowJobCheckpoint : BaseEntity
{
    /// <summary>Owning workflow checkpoint identifier.</summary>
    public Guid GitHubWorkflowCheckpointId { get; set; }

    /// <summary>Related workflow checkpoint entity.</summary>
    public GitHubWorkflowCheckpoint WorkflowCheckpoint { get; set; } = null!;

    /// <summary>GitHub job identifier.</summary>
    public long JobId { get; set; }

    /// <summary>Job name used to match final archive entries.</summary>
    public string JobName { get; set; } = string.Empty;

    /// <summary>Digest of the last published job and step state snapshot.</summary>
    public string? LastStateFingerprint { get; set; }

    /// <summary>Number of normalized log lines already published.</summary>
    public int LastLogLineCount { get; set; }

    /// <summary>Digest of the published line prefix for deduplication.</summary>
    public string? LastLogPrefixHash { get; set; }

    /// <summary>Digest of the most recently checkpointed job log content.</summary>
    public string? LastLogContentHash { get; set; }

    /// <summary>Digest of the final workflow archive entry set for this job.</summary>
    public string? FinalArchiveContentHash { get; set; }

    /// <summary>Most recent GitHub job-log download outcome.</summary>
    public GitHubWorkflowLogAvailability LogAvailability { get; set; }

    /// <summary>Whether all available output for this job has been reconciled.</summary>
    public bool IsLogFinal { get; set; }

    /// <summary>Time of the most recent job-log download attempt.</summary>
    public DateTimeOffset? LastLogCheckedAt { get; set; }
}