using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Application.Diagnostics;

/// <summary>
/// Owns the stable OpenTelemetry source names emitted by AutoMate deployment workflows.
/// </summary>
public static class AutoMateTelemetry
{
    /// <summary>Activity source for deployment collection, redaction, and terminal delivery.</summary>
    public static readonly ActivitySource Deployments = new("AutoMate.Deployments");

    /// <summary>Meter for deployment collection and delivery health.</summary>
    public static readonly Meter Meter = new("AutoMate.Deployments");

    /// <summary>Number of diagnostics accepted by the bounded pipeline.</summary>
    public static readonly Counter<long> EventsReceived = Meter.CreateCounter<long>("automate.deployment.diagnostics.received");

    /// <summary>Number of diagnostics delivered to a terminal transport.</summary>
    public static readonly Counter<long> EventsDelivered = Meter.CreateCounter<long>("automate.deployment.diagnostics.delivered");

    /// <summary>Number of diagnostics rejected because the bounded pipeline was full.</summary>
    public static readonly Counter<long> EventsDropped = Meter.CreateCounter<long>("automate.deployment.diagnostics.dropped");

    /// <summary>Number of values changed by diagnostic redaction.</summary>
    public static readonly Counter<long> ValuesRedacted = Meter.CreateCounter<long>("automate.deployment.diagnostics.redacted");

    /// <summary>Number of terminal delivery failures.</summary>
    public static readonly Counter<long> DeliveryFailures = Meter.CreateCounter<long>("automate.deployment.diagnostics.delivery_failures");

    /// <summary>Number of redacted diagnostics persisted for bounded replay and context construction.</summary>
    public static readonly Counter<long> EventsPersisted = Meter.CreateCounter<long>("automate.deployment.diagnostics.persisted");

    /// <summary>Number of diagnostic persistence failures isolated from collection and delivery.</summary>
    public static readonly Counter<long> PersistenceFailures = Meter.CreateCounter<long>("automate.deployment.diagnostics.persistence_failures");

    /// <summary>Duration of redaction and bounded diagnostic ingestion.</summary>
    public static readonly Histogram<double> IngestDuration = Meter.CreateHistogram<double>(
        "automate.deployment.diagnostics.ingest.duration", unit: "ms");

    /// <summary>Duration of persistence and terminal sink operations.</summary>
    public static readonly Histogram<double> SinkDuration = Meter.CreateHistogram<double>(
        "automate.deployment.diagnostics.sink.duration", unit: "ms");
}
