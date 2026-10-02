namespace Domain.Entities;

/// <summary>Transactional-outbox work item for a requested analysis.</summary>
public sealed class DeploymentAnalysisWorkItem : BaseEntity
{
    public Guid AnalysisId { get; set; }
    public AiDeploymentAnalysis Analysis { get; set; } = null!;
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}