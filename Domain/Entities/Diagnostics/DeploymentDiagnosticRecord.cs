namespace Domain.Entities;

/// <summary>
///     Legacy redacted diagnostic history and retained delivery-buffer metadata.
/// </summary>
public sealed class DeploymentDiagnosticRecord : BaseEntity
{
    /// <summary>Owner identity for storage routing, never accepted from client input.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    ///     Serialized safe envelope for specialized delivery; null denotes legacy PostgreSQL
    ///     history.
    /// </summary>
    public string? DeliveryJson { get; set; }

    /// <summary>UTF-8 buffer budget accounting.</summary>
    public int DeliveryBytes { get; set; }

    /// <summary>Stable ingestion timestamp used for Loki ordering.</summary>
    public DateTimeOffset? StoredAt { get; set; }

    /// <summary>Whether the batch has been accepted and is waiting for query visibility.</summary>
    public bool DeliveryAccepted { get; set; }

    /// <summary>Absolute deadline for short-term buffering.</summary>
    public DateTimeOffset? BufferExpiresAt { get; set; }

    /// <summary>
    ///     Legacy numeric sample or pending delivery metadata; new writes use the telemetry
    ///     gateway.
    /// </summary>
    public string? MetricSamplesJson { get; set; }

    /// <summary>Redacted container/provider identity.</summary>
    public string? SourceIdentityJson { get; set; }

    /// <summary>Durable ordering cursor used by legacy terminal replay.</summary>
    public long OrderId { get; set; }

    /// <summary>Related deployment identity; null permits legacy project-only records.</summary>
    public Guid? DeploymentId { get; set; }

    /// <summary>Related deployment entity, if the record has deployment identity.</summary>
    public Deployment? Deployment { get; set; }

    /// <summary>Project identity used to scope history reads.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>Original UTC time assigned by the normalizing provider adapter.</summary>
    public DateTimeOffset TimestampUtc { get; set; }

    /// <summary>Recorded finite provider-adapter classification.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Recorded observation category, such as log, lifecycle or metric.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Recorded normalized severity classification.</summary>
    public string Severity { get; set; } = string.Empty;

    /// <summary>Redacted and bounded diagnostic message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Stable terminal routing channel, or null for records without terminal output.</summary>
    public string? TerminalChannel { get; set; }

    /// <summary>Serialized bounded redacted diagnostic attributes, if present.</summary>
    public string? AttributesJson { get; set; }

    /// <summary>Optional canonical trace identity retained for correlation.</summary>
    public string? TraceId { get; set; }

    /// <summary>Optional canonical span identity retained for correlation.</summary>
    public string? SpanId { get; set; }

    /// <summary>Optional provider-local observation sequence.</summary>
    public long? Sequence { get; set; }

    /// <summary>Optional bounded source cursor; contains no credentials.</summary>
    public string? Cursor { get; set; }

    /// <summary>Deadline after which this legacy record is eligible for retention cleanup.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}