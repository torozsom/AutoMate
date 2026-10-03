namespace Domain.Entities;

/// <summary>Redacted normalized diagnostic retained for terminal replay and bounded AI context.</summary>
public sealed class DeploymentDiagnosticRecord : BaseEntity
{
    /// <summary>Owner identity for storage routing, never accepted from client input.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Serialized safe envelope for specialized delivery; null denotes legacy PostgreSQL history.</summary>
    public string? DeliveryJson { get; set; }

    /// <summary>UTF-8 buffer budget accounting.</summary>
    public int DeliveryBytes { get; set; }

    /// <summary>Stable ingestion timestamp used for Loki ordering.</summary>
    public DateTimeOffset? StoredAt { get; set; }

    /// <summary>Whether the batch has been accepted and is waiting for query visibility.</summary>
    public bool DeliveryAccepted { get; set; }

    /// <summary>Absolute deadline for short-term buffering.</summary>
    public DateTimeOffset? BufferExpiresAt { get; set; }

    /// <summary>Typed numeric observations for PostgreSQL fallback and temporary delivery.</summary>
    public string? MetricSamplesJson { get; set; }

    /// <summary>Redacted container/provider identity.</summary>
    public string? SourceIdentityJson { get; set; }

    public long OrderId { get; set; }
    public Guid? DeploymentId { get; set; }
    public Deployment? Deployment { get; set; }
    public Guid ProjectId { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? TerminalChannel { get; set; }
    public string? AttributesJson { get; set; }
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }
    public long? Sequence { get; set; }
    public string? Cursor { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}