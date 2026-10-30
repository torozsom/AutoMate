namespace Domain.Entities.Diagnostics;

/// <summary>
///     Durable tenant delivery lease and loss accounting, without diagnostic payloads.
/// </summary>
public sealed class TelemetryTenantState : BaseEntity
{
    /// <summary>Owner whose stream is serialized by this lease.</summary>
    public Guid TenantId { get; init; }

    /// <summary>Worker fencing token.</summary>
    public Guid? LeaseId { get; init; }

    /// <summary>Lease recovery deadline.</summary>
    public DateTimeOffset? LeaseUntil { get; init; }

    /// <summary>Next retry time including bounded provider backoff.</summary>
    public DateTimeOffset DueAt { get; set; }

    /// <summary>Consecutive failure count.</summary>
    public int Attempts { get; init; }

    /// <summary>Latest assigned ingestion time, preserving per-tenant order.</summary>
    public DateTimeOffset LastStoredAt { get; init; }

    /// <summary>Owner-wide lost records reported on history reads.</summary>
    public long DroppedEvents { get; init; }

    /// <summary>Cluster-wide ingestion rate window start.</summary>
    public DateTimeOffset RateWindowStart { get; init; }

    /// <summary>Bytes admitted in the current rate window, including already delivered data.</summary>
    public long RateWindowBytes { get; init; }

    /// <summary>Atomic spool accounting; the empty-tenant row holds the global total.</summary>
    public long BufferedBytes { get; set; }

    /// <summary>Bounded metric deployment/container identities and their expiry deadlines.</summary>
    public string MetricIdentitiesJson { get; init; } = "{}";
}
