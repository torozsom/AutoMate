using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Application.Diagnostics;

/// <summary>
///     Owns the stable OpenTelemetry source names emitted by AutoMate deployment workflows.
/// </summary>
public static class AutoMateTelemetry
{
    private static long _cloudQueued;
    private static long _cloudActive;
    private static long _cloudWebhookBacklog;
    private static long _cloudReconciliationBacklog;
    private static long _cloudOldestQueueAgeMs;
    /// <summary>Activity source for security-sensitive request outcomes.</summary>
    public static readonly ActivitySource Security = new("AutoMate.Security");

    /// <summary>Activity source for deployment collection, redaction, and terminal delivery.</summary>
    public static readonly ActivitySource Deployments = new("AutoMate.Deployments");

    /// <summary>Meter for security-sensitive request outcomes.</summary>
    public static readonly Meter SecurityMeter = new("AutoMate.Security");

    /// <summary>Meter for deployment collection and delivery health.</summary>
    public static readonly Meter DeploymentMeter = new("AutoMate.Deployments");

    public static readonly UpDownCounter<long> DeploymentJobsQueued = DeploymentMeter.CreateUpDownCounter<long>(
        "automate.deployment.jobs.queued");
    public static readonly UpDownCounter<long> DeploymentJobsActive = DeploymentMeter.CreateUpDownCounter<long>(
        "automate.deployment.jobs.active");
    public static readonly Counter<long> DeploymentJobsStarted = DeploymentMeter.CreateCounter<long>(
        "automate.deployment.jobs.started");
    public static readonly Counter<long> DeploymentJobsCompleted = DeploymentMeter.CreateCounter<long>(
        "automate.deployment.jobs.completed");
    public static readonly Counter<long> DeploymentJobsFailed = DeploymentMeter.CreateCounter<long>(
        "automate.deployment.jobs.failed");
    public static readonly Histogram<double> DeploymentQueueWait = DeploymentMeter.CreateHistogram<double>(
        "automate.deployment.jobs.queue_wait", "ms");

    /// <summary>Accepted durable SaaS cloud requests.</summary>
    public static readonly Counter<long> CloudRunsAdmitted = DeploymentMeter.CreateCounter<long>(
        "automate.cloud.runs.admitted");
    /// <summary>Requests refused by the per-user queue bound.</summary>
    public static readonly Counter<long> CloudRunsRejected = DeploymentMeter.CreateCounter<long>(
        "automate.cloud.runs.rejected");
    /// <summary>Cloud workflows that reached a successful terminal phase.</summary>
    public static readonly Counter<long> CloudRunsSucceeded = DeploymentMeter.CreateCounter<long>(
        "automate.cloud.runs.succeeded");
    /// <summary>Cloud runs that reached a failed terminal phase.</summary>
    public static readonly Counter<long> CloudRunsFailed = DeploymentMeter.CreateCounter<long>(
        "automate.cloud.runs.failed");
    /// <summary>Cloud workflows that exceeded the monitoring window.</summary>
    public static readonly Counter<long> CloudRunsTimedOut = DeploymentMeter.CreateCounter<long>(
        "automate.cloud.runs.timed_out");
    /// <summary>Launch attempts begun after admission.</summary>
    public static readonly Counter<long> CloudLaunchesStarted = DeploymentMeter.CreateCounter<long>(
        "automate.cloud.launches.started");
    /// <summary>Launches recovered after a prior worker lease expired.</summary>
    public static readonly Counter<long> CloudLeasesRecovered = DeploymentMeter.CreateCounter<long>(
        "automate.cloud.leases.recovered");
    /// <summary>Provider throttling observations.</summary>
    public static readonly Counter<long> CloudProviderThrottles = DeploymentMeter.CreateCounter<long>(
        "automate.cloud.provider.throttles");
    /// <summary>Time from durable admission to first launch attempt.</summary>
    public static readonly Histogram<double> CloudQueueWait = DeploymentMeter.CreateHistogram<double>(
        "automate.cloud.queue.wait", "ms");
    /// <summary>Time spent in the active launch phase.</summary>
    public static readonly Histogram<double> CloudLaunchDuration = DeploymentMeter.CreateHistogram<double>(
        "automate.cloud.launch.duration", "ms");
    /// <summary>Time a verified webhook waited in the durable inbox.</summary>
    public static readonly Histogram<double> CloudWebhookLag = DeploymentMeter.CreateHistogram<double>(
        "automate.cloud.webhook.lag", "ms");
    /// <summary>Cluster queue depth sampled from PostgreSQL; aggregate replicas with max.</summary>
    public static readonly ObservableGauge<long> CloudQueued = DeploymentMeter.CreateObservableGauge(
        "automate.cloud.queue.depth", () => Interlocked.Read(ref _cloudQueued));
    /// <summary>Cluster launch leases sampled from PostgreSQL; aggregate replicas with max.</summary>
    public static readonly ObservableGauge<long> CloudActive = DeploymentMeter.CreateObservableGauge(
        "automate.cloud.launches.active", () => Interlocked.Read(ref _cloudActive));
    /// <summary>Unprocessed signed webhook receipts sampled from PostgreSQL.</summary>
    public static readonly ObservableGauge<long> CloudWebhookBacklog = DeploymentMeter.CreateObservableGauge(
        "automate.cloud.webhook.backlog", () => Interlocked.Read(ref _cloudWebhookBacklog));
    /// <summary>Runs due for missed-event reconciliation.</summary>
    public static readonly ObservableGauge<long> CloudReconciliationBacklog = DeploymentMeter.CreateObservableGauge(
        "automate.cloud.reconciliation.backlog", () => Interlocked.Read(ref _cloudReconciliationBacklog));
    /// <summary>Age of the oldest queued run in milliseconds.</summary>
    public static readonly ObservableGauge<long> CloudOldestQueueAge = DeploymentMeter.CreateObservableGauge(
        "automate.cloud.queue.oldest_age", () => Interlocked.Read(ref _cloudOldestQueueAgeMs), "ms");

    /// <summary>Publishes a bounded database snapshot to the process's observable gauges.</summary>
    public static void SetCloudControlPlaneSnapshot(long queued, long active, long webhookBacklog,
        long reconciliationBacklog, long oldestQueueAgeMs)
    {
        Interlocked.Exchange(ref _cloudQueued, queued);
        Interlocked.Exchange(ref _cloudActive, active);
        Interlocked.Exchange(ref _cloudWebhookBacklog, webhookBacklog);
        Interlocked.Exchange(ref _cloudReconciliationBacklog, reconciliationBacklog);
        Interlocked.Exchange(ref _cloudOldestQueueAgeMs, oldestQueueAgeMs);
    }

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
