using Domain.Enums;

namespace Domain.Entities;

/// <summary>Stores the status and redacted result of one requested deployment analysis.</summary>
public sealed class AiDeploymentAnalysis : BaseEntity
{
    public Guid DeploymentId { get; set; }
    public Deployment Deployment { get; set; } = null!;
    public AiAnalysisTrigger Trigger { get; set; }
    public AiAnalysisStatus Status { get; set; }
    public string? Result { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
