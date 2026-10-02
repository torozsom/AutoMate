using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Application.Diagnostics;

/// <summary>
///     Owns the stable OpenTelemetry source names emitted by AutoMate deployment workflows.
/// </summary>
public static class AutoMateTelemetry
{
    /// <summary>Activity source for security-sensitive request outcomes.</summary>
    public static readonly ActivitySource Security = new("AutoMate.Security");

    /// <summary>Activity source for deployment collection, redaction, and terminal delivery.</summary>
    public static readonly ActivitySource Deployments = new("AutoMate.Deployments");

    /// <summary>Meter for security-sensitive request outcomes.</summary>
    public static readonly Meter SecurityMeter = new("AutoMate.Security");

    /// <summary>Meter for deployment collection and delivery health.</summary>
    public static readonly Meter DeploymentMeter = new("AutoMate.Deployments");

    /// <summary>Number of requests rejected by the global rate limiter.</summary>
    public static readonly Counter<long> RateLimitRejections = SecurityMeter.CreateCounter<long>(
        "automate.security.rate_limit.rejections");

    /// <summary>Number of diagnostics accepted by the bounded pipeline.</summary>
    public static readonly Counter<long> EventsReceived =
        DeploymentMeter.CreateCounter<long>("automate.deployment.diagnostics.received");

    /// <summary>Number of diagnostics delivered to a terminal transport.</summary>
    public static readonly Counter<long> EventsDelivered =
        DeploymentMeter.CreateCounter<long>("automate.deployment.diagnostics.delivered");

    /// <summary>Number of diagnostics rejected because the bounded pipeline was full.</summary>
    public static readonly Counter<long> EventsDropped =
        DeploymentMeter.CreateCounter<long>("automate.deployment.diagnostics.dropped");

    /// <summary>Number of values changed by diagnostic redaction.</summary>
    public static readonly Counter<long> ValuesRedacted =
        DeploymentMeter.CreateCounter<long>("automate.deployment.diagnostics.redacted");

    /// <summary>Number of terminal delivery failures.</summary>
    public static readonly Counter<long> DeliveryFailures =
        DeploymentMeter.CreateCounter<long>("automate.deployment.diagnostics.delivery_failures");

    /// <summary>Number of redacted diagnostics persisted for bounded replay and context construction.</summary>
    public static readonly Counter<long> EventsPersisted =
        DeploymentMeter.CreateCounter<long>("automate.deployment.diagnostics.persisted");

    /// <summary>Number of diagnostic persistence failures isolated from collection and delivery.</summary>
    public static readonly Counter<long> PersistenceFailures =
        DeploymentMeter.CreateCounter<long>("automate.deployment.diagnostics.persistence_failures");

    /// <summary>Duration of redaction and bounded diagnostic ingestion.</summary>
    public static readonly Histogram<double> IngestDuration = DeploymentMeter.CreateHistogram<double>(
        "automate.deployment.diagnostics.ingest.duration", "ms");

    /// <summary>Duration of persistence and terminal sink operations.</summary>
    public static readonly Histogram<double> SinkDuration = DeploymentMeter.CreateHistogram<double>(
        "automate.deployment.diagnostics.sink.duration", "ms");
}