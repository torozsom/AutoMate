namespace Domain.Entities;

/// <summary>Small, rebuildable daily aggregates; never contains raw diagnostic payloads.</summary>
public sealed class DeploymentDailyTelemetry : BaseEntity
{
    public Guid UserId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid DeploymentId { get; set; }
    public DateTimeOffset DayUtc { get; set; }
    public string Container { get; set; } = "";
    public string Metric { get; set; } = "";
    public string Unit { get; set; } = "";
    public long SampleCount { get; set; }
    public double Sum { get; set; }
    public double? Minimum { get; set; }
    public double? Maximum { get; set; }
    public long ObservedErrors { get; set; }
    public bool Incomplete { get; set; }
}