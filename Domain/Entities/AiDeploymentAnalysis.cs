using Domain.Enums;

namespace Domain.Entities;

/// <summary>Stores the status and redacted result of one requested deployment analysis.</summary>
public sealed class AiDeploymentAnalysis : BaseEntity
{
    public Guid DeploymentId { get; set; }
    public Deployment Deployment { get; set; } = null!;
    public AiAnalysisTrigger Trigger { get; set; }
    public AiAnalysisStatus Status { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public string? Summary { get; set; }
    public string? RecommendedStepsJson { get; set; }
    public string? EvidenceReferencesJson { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}