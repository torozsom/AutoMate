using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Application.Diagnostics;

/// <summary>
///     Records finite operation outcomes without payloads, endpoints or customer identifiers.
/// </summary>
public static class OperationalMetrics
{
    /// <summary>Reviewed meter identity shared by the hosts.</summary>
    public const string MeterName = "AutoMate.Operations";

    /// <summary>Operation instrumentation independent of the OTel SDK.</summary>
    private static readonly Meter Meter = new(MeterName);

    /// <summary>Completed operations by source, stage and outcome.</summary>
    private static readonly Counter<long> Operations = Meter.CreateCounter<long>("automate.operation.count");

    /// <summary>Operation latency in milliseconds.</summary>
    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("automate.operation.duration", "ms");

    /// <summary>Live observations accepted from Docker collection.</summary>
    public static readonly Counter<long> LiveObservations = Meter.CreateCounter<long>("automate.live.observations");

    /// <summary>Pending live observations replaced by fresher measurements.</summary>
    public static readonly Counter<long> Coalesced = Meter.CreateCounter<long>("automate.live.coalesced");

    /// <summary>Sampled durable-history admission attempts.</summary>
    public static readonly Counter<long> HistoryAdmissions =
        Meter.CreateCounter<long>("automate.live.history.admissions");

    /// <summary>
    ///     Records an operation using only emitter-owned finite labels.
    /// </summary>
    /// <param name="source">Reviewed source family.</param>
    /// <param name="stage">Reviewed operation stage.</param>
    /// <param name="outcome">Reviewed terminal outcome.</param>
    /// <param name="statusClass">Fixed HTTP status class, or none for other operations.</param>
    /// <param name="started">Monotonic start timestamp.</param>
    public static void Record(string source, string stage, string outcome, long started, string statusClass = "none")
    {
        source = source is "AutoMate" or "SignalR" or "DockerContainer" or "DockerCompose" or "DockerDaemon"
            or "GitHubActions" or "AzureContainerApps" or
            "AzureRuntime" or "AzureLiveLogs" or "GitHub" or "Analysis" or "DeploymentTelemetry"
            ? source
            : "Unknown";
        stage = stage is "send" or "typed_metrics" or "legacy_metrics" or "discovery" or "connection" or "reading" or
                    "publication" or "request_headers" or "stream_headers" or "dispatch" ||
                (Enum.TryParse<DeploymentOperation>(stage, out var operation) && Enum.IsDefined(operation) &&
                 operation.ToString() == stage)
            ? stage
            : "Unknown";
        outcome = outcome is "completed" or "failed" or "canceled" or "timed_out" or "denied" or "slow" or
            "unsuccessful" or "transport_failure" or "shutdown" or "ownership_refresh" or "timeout" or "remote_eof" or
            "recovered" or "provider_conflict" or "provider_rejected"
            ? outcome
            : "Unknown";
        statusClass = statusClass is "none" or "1xx" or "2xx" or "3xx" or "4xx" or "5xx" or "other"
            ? statusClass
            : "none";
        var tags = new TagList
            { { "source", source }, { "stage", stage }, { "outcome", outcome }, { "status_class", statusClass } };
        Operations.Add(1, tags);
        Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
    }
}
