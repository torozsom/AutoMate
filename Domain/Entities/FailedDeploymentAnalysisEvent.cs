namespace Domain.Entities;

/// <summary>One durable metadata-only failure wakeup per deployment, retained until deployment deletion.</summary>
public sealed class FailedDeploymentAnalysisEvent
{
    /// <summary>Deployment identity and primary key; prevents repeated failures from creating another automatic request.</summary>
    public Guid DeploymentId { get; set; }

    /// <summary>Owning deployment; deletion removes the wakeup and its completion marker.</summary>
    public Deployment Deployment { get; set; } = null!;

    /// <summary>Database UTC time of the first persisted failure.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Admission or policy denial completion; null remains recoverable after a worker restart.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
}