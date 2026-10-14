namespace Domain.Entities.Diagnostics;

/// <summary>
///     Retained daily sufficient statistics for recorded container metrics and observed log
///     errors.
/// </summary>
public sealed class DeploymentDailyTelemetry : BaseEntity
{
    /// <summary>Owner-account identity used to scope the aggregate.</summary>
    public Guid UserId { get; set; }

    /// <summary>Application identity associated with the recorded observations.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>Deployment identity associated with the aggregate.</summary>
    public Guid DeploymentId { get; set; }

    /// <summary>UTC day represented by these sufficient statistics.</summary>
    public DateTimeOffset DayUtc { get; set; }

    /// <summary>Recorded container or resource identity for the series.</summary>
    public string Container { get; set; } = "";

    /// <summary>Fixed provider-neutral metric-series name.</summary>
    public string Metric { get; set; } = "";

    /// <summary>Native unit associated with the metric values.</summary>
    public string Unit { get; set; } = "";

    /// <summary>Number of underlying observations represented by the aggregate.</summary>
    public long SampleCount { get; set; }

    /// <summary>Sum of observed values, used with sample count for weighted averages.</summary>
    public double Sum { get; set; }

    /// <summary>Lowest observed value, or null when unavailable.</summary>
    public double? Minimum { get; set; }

    /// <summary>Highest observed value, or null when unavailable.</summary>
    public double? Maximum { get; set; }

    /// <summary>Recorded log-error count; not a service-wide error-rate estimate.</summary>
    public long ObservedErrors { get; set; }

    /// <summary>Whether the aggregate has known observation or delivery gaps.</summary>
    public bool Incomplete { get; set; }
}
