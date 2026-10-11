using System.Diagnostics.Metrics;

namespace Infrastructure.Diagnostics;

/// <summary>Low-cardinality operational measurements for specialized storage.</summary>
internal static class TelemetryStorageMetrics
{
    /// <summary>Meter exported by the existing OpenTelemetry configuration.</summary>
    internal static readonly Meter Meter = new("AutoMate.TelemetryStorage");

    /// <summary>Accepted redacted bytes.</summary>
    internal static readonly Counter<long> IngestedBytes = Meter.CreateCounter<long>("telemetry.ingested.bytes");

    /// <summary>Explicitly omitted diagnostic events.</summary>
    internal static readonly Counter<long> Dropped = Meter.CreateCounter<long>("telemetry.dropped.events");

    /// <summary>Lease recovery and provider retry counts.</summary>
    internal static readonly Counter<long> Retries = Meter.CreateCounter<long>("telemetry.delivery.retries");

    /// <summary>Current aggregate short-term spool size.</summary>
    internal static readonly Histogram<long> SpoolBytes = Meter.CreateHistogram<long>("telemetry.spool.bytes");

    /// <summary>Time until durable events become query-visible.</summary>
    internal static readonly Histogram<double> VisibilitySeconds =
        Meter.CreateHistogram<double>("telemetry.visibility.seconds");

    /// <summary>Store request duration, excluding admission wait.</summary>
    internal static readonly Histogram<double> RequestSeconds =
        Meter.CreateHistogram<double>("telemetry.request.seconds");

    /// <summary>Provider throttle responses, without tenant or URL labels.</summary>
    internal static readonly Counter<long> Throttled = Meter.CreateCounter<long>("telemetry.provider.throttles");

    /// <summary>Age of payloads past their bounded spool deadline.</summary>
    internal static readonly Histogram<double> CleanupOverdueSeconds =
        Meter.CreateHistogram<double>("telemetry.cleanup.overdue.seconds");
}