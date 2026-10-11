namespace Domain.Entities;

/// <summary>
///     Stores a durable, non-sensitive cursor for one Azure Container Apps log source.
/// </summary>
public sealed class AzureContainerAppLogCheckpoint : BaseEntity
{
    /// <summary>Deployment whose runtime logs are being tailed.</summary>
    public Guid DeploymentId { get; set; }

    /// <summary>Owning deployment.</summary>
    public Deployment Deployment { get; set; } = null!;

    /// <summary>Logical source name: <c>console</c>, <c>system</c>, or a bounded <c>db-</c> resource hash.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Timestamp of the last delivered record.</summary>
    public DateTimeOffset? LastTimestamp { get; set; }

    /// <summary>Last durably accepted Azure ingestion time, independent of delayed event timestamps.</summary>
    public DateTimeOffset? LastIngestionTimestamp { get; set; }

    /// <summary>Stable ordering hash for records sharing an ingestion time.</summary>
    public string? LastIngestionTieBreaker { get; set; }

    /// <summary>Deterministic hash used to order records with the same timestamp.</summary>
    public string? LastTieBreaker { get; set; }

    /// <summary>When the source was last successfully queried.</summary>
    public DateTimeOffset? LastSuccessfulQueryAt { get; set; }
}