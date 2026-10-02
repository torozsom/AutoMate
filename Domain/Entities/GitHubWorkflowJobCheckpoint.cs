using Domain.Enums;

namespace Domain.Entities;

/// <summary>Stores a redacted-log checkpoint and state fingerprint for one GitHub Actions job.</summary>
public sealed class GitHubWorkflowJobCheckpoint : BaseEntity
{
    public Guid GitHubWorkflowCheckpointId { get; set; }
    public GitHubWorkflowCheckpoint WorkflowCheckpoint { get; set; } = null!;
    public long JobId { get; set; }
    public string JobName { get; set; } = string.Empty;
    public string? LastStateFingerprint { get; set; }
    public int LastLogLineCount { get; set; }
    public string? LastLogPrefixHash { get; set; }
    public string? LastLogContentHash { get; set; }
    public string? FinalArchiveContentHash { get; set; }
    public GitHubWorkflowLogAvailability LogAvailability { get; set; }
    public bool IsLogFinal { get; set; }
    public DateTimeOffset? LastLogCheckedAt { get; set; }
}