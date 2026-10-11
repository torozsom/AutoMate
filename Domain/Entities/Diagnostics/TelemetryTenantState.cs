namespace Domain.Entities;

/// <summary>Durable tenant delivery lease and loss accounting, without diagnostic payloads.</summary>
public sealed class TelemetryTenantState : BaseEntity
{
    /// <summary>Owner whose stream is serialized by this lease.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Worker fencing token.</summary>
    public Guid? LeaseId { get; set; }

    /// <summary>Lease recovery deadline.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    /// <summary>Next retry time including bounded provider backoff.</summary>
    public DateTimeOffset DueAt { get; set; }

    /// <summary>Consecutive failure count.</summary>
    public int Attempts { get; set; }

    /// <summary>Latest assigned ingestion time, preserving per-tenant order.</summary>
    public DateTimeOffset LastStoredAt { get; set; }

    /// <summary>Owner-wide lost records reported on history reads.</summary>
    public long DroppedEvents { get; set; }

    /// <summary>Cluster-wide ingestion rate window start.</summary>
    public DateTimeOffset RateWindowStart { get; set; }

    /// <summary>Bytes admitted in the current rate window, including already delivered data.</summary>
    public long RateWindowBytes { get; set; }

    /// <summary>Atomic spool accounting; the empty-tenant row holds the global total.</summary>
    public long BufferedBytes { get; set; }

    /// <summary>Bounded metric deployment/container identities and their expiry deadlines.</summary>
    public string MetricIdentitiesJson { get; set; } = "{}";
}