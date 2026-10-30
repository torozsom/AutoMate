using Domain.Entities.Deployments;

namespace Domain.Entities.Diagnostics;

/// <summary>
///     Legacy redacted diagnostic history and retained delivery-buffer metadata.
/// </summary>
public sealed class DeploymentDiagnosticRecord : BaseEntity
{
    /// <summary>Owner identity for storage routing, never accepted from client input.</summary>
    public Guid? TenantId { get; init; }

    /// <summary>
    ///     Serialized safe envelope for specialized delivery; null denotes legacy PostgreSQL
    ///     history.
    /// </summary>
    public string? DeliveryJson { get; init; }

    /// <summary>UTF-8 buffer budget accounting.</summary>
    public int DeliveryBytes { get; init; }

    /// <summary>Stable ingestion timestamp used for Loki ordering.</summary>
    public DateTimeOffset? StoredAt { get; init; }

    /// <summary>Whether the batch has been accepted and is waiting for query visibility.</summary>
    public bool DeliveryAccepted { get; init; }

    /// <summary>Absolute deadline for short-term buffering.</summary>
    public DateTimeOffset? BufferExpiresAt { get; init; }

    /// <summary>
    ///     Legacy numeric sample or pending delivery metadata; new writes use the telemetry
    ///     gateway.
    /// </summary>
    public string? MetricSamplesJson { get; init; }

    /// <summary>Redacted container/provider identity.</summary>
    public string? SourceIdentityJson { get; init; }

    /// <summary>Durable ordering cursor used by legacy terminal replay.</summary>
    public long OrderId { get; init; }

    /// <summary>Related deployment identity; null permits legacy project-only records.</summary>
    public Guid? DeploymentId { get; init; }

    /// <summary>Related deployment entity, if the record has deployment identity.</summary>
    public Deployment? Deployment { get; init; }

    /// <summary>Project identity used to scope history reads.</summary>
    public Guid ProjectId { get; init; }

    /// <summary>Original UTC time assigned by the normalizing provider adapter.</summary>
    public DateTimeOffset TimestampUtc { get; init; }

    /// <summary>Recorded finite provider-adapter classification.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Recorded observation category, such as log, lifecycle or metric.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Recorded normalized severity classification.</summary>
    public string Severity { get; init; } = string.Empty;

    /// <summary>Redacted and bounded diagnostic message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Stable terminal routing channel, or null for records without terminal output.</summary>
    public string? TerminalChannel { get; init; }

    /// <summary>Serialized bounded redacted diagnostic attributes, if present.</summary>
    public string? AttributesJson { get; init; }

    /// <summary>Optional canonical trace identity retained for correlation.</summary>
    public string? TraceId { get; init; }

    /// <summary>Optional canonical span identity retained for correlation.</summary>
    public string? SpanId { get; init; }

    /// <summary>Optional provider-local observation sequence.</summary>
    public long? Sequence { get; init; }

    /// <summary>Optional bounded source cursor; contains no credentials.</summary>
    public string? Cursor { get; init; }

    /// <summary>Deadline after which this legacy record is eligible for retention cleanup.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}
