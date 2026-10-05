using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;

namespace Web.Observability;

/// <summary>Approves metric identities and removes request/provider-derived dimensions before aggregation.</summary>
public static class SafeMetricPolicy
{
    /// <summary>Fixed instruments owned by AutoMate; their remaining dimensions are emitted by reviewed finite sources.</summary>
    private static readonly HashSet<string> ApplicationNames = new(StringComparer.Ordinal)
    {
        "automate.analysis.operations",
        "automate.analysis.duration",
        "automate.analysis.active",
        "automate.analysis.queue.wait",
        "automate.analysis.input.tokens",
        "automate.analysis.output.tokens",
        "automate.cloud.launch.duration",
        "automate.cloud.launches.active",
        "automate.cloud.launches.started",
        "automate.cloud.leases.recovered",
        "automate.cloud.provider.throttles",
        "automate.cloud.queue.depth",
        "automate.cloud.queue.oldest_age",
        "automate.cloud.queue.wait",
        "automate.cloud.reconciliation.backlog",
        "automate.cloud.runs.admitted",
        "automate.cloud.runs.failed",
        "automate.cloud.runs.rejected",
        "automate.cloud.runs.succeeded",
        "automate.cloud.runs.timed_out",
        "automate.cloud.webhook.backlog",
        "automate.cloud.webhook.lag",
        "automate.deployment.collector.errors",
        "automate.deployment.collector.reconnects",
        "automate.deployment.diagnostics.cursor.lag",
        "automate.deployment.diagnostics.delivered",
        "automate.deployment.diagnostics.delivery_failures",
        "automate.deployment.diagnostics.dropped",
        "automate.deployment.diagnostics.duplicates",
        "automate.deployment.diagnostics.ingest.duration",
        "automate.deployment.diagnostics.persisted",
        "automate.deployment.diagnostics.persistence_failures",
        "automate.deployment.diagnostics.queue.depth",
        "automate.deployment.diagnostics.received",
        "automate.deployment.diagnostics.redacted",
        "automate.deployment.diagnostics.sink.duration",
        "automate.deployment.jobs.active",
        "automate.deployment.jobs.completed",
        "automate.deployment.jobs.failed",
        "automate.deployment.jobs.queue_wait",
        "automate.deployment.jobs.queued",
        "automate.deployment.jobs.started",
        "automate.platform.spans.omitted",
        "automate.security.rate_limit.rejections",
        "telemetry.cleanup.overdue.seconds",
        "telemetry.delivery.retries",
        "telemetry.dropped.events",
        "telemetry.ingested.bytes",
        "telemetry.provider.throttles",
        "telemetry.request.seconds",
        "telemetry.spool.bytes",
        "telemetry.visibility.seconds"
    };

    /// <summary>Known framework instruments; all external labels are omitted, retaining aggregate measurements.</summary>
    private static readonly HashSet<string> FrameworkNames = new(StringComparer.Ordinal)
    {
        "http.server.request.duration", "http.client.request.duration", "http.client.active_requests",
        "http.client.open_connections", "http.client.connection.duration", "http.client.request.time_in_queue",
        "http.client.request.body.size", "http.client.response.body.size",
        "dotnet.gc.collections", "dotnet.gc.heap.total_allocated", "dotnet.gc.last_collection.heap.size",
        "dotnet.gc.last_collection.heap.fragmentation.size", "dotnet.gc.last_collection.memory.committed_size",
        "dotnet.gc.pause.time", "dotnet.jit.compiled_il.size", "dotnet.jit.compiled_methods",
        "dotnet.jit.compilation.time", "dotnet.monitor.lock_contentions", "dotnet.thread_pool.thread.count",
        "dotnet.thread_pool.work_item.count", "dotnet.thread_pool.queue.length", "dotnet.timer.count",
        "dotnet.assembly.count", "dotnet.exceptions", "dotnet.process.memory.working_set", "dotnet.process.cpu.time"
    };

    /// <summary>Configures views before exporters and explicitly disables exemplars, which otherwise retain removed tags.</summary>
    public static void Configure(MeterProviderBuilder builder)
    {
        builder.SetExemplarFilter(ExemplarFilterType.AlwaysOff);
        builder.AddView(View);
    }

    /// <summary>Drops unfamiliar identities; filters dimensions before they can enter SDK aggregation or export.</summary>
    private static MetricStreamConfiguration View(Instrument instrument)
    {
        if (instrument.Meter.Name is "AutoMate.Deployments" or "AutoMate.Security" or "AutoMate.TelemetryStorage"
                or "AutoMate.Analysis" &&
            ApplicationNames.Contains(instrument.Name))
            return new MetricStreamConfiguration
            {
                TagKeys = Dimensions(instrument.Name),
                CardinalityLimit = 4096
            };
        if (instrument.Meter.Name is "System.Runtime" or "System.Net.Http" or "Microsoft.AspNetCore.Hosting" &&
            FrameworkNames.Contains(instrument.Name))
            return new MetricStreamConfiguration
            {
                TagKeys = instrument.Meter.Name == "System.Runtime" ? RuntimeDimensions(instrument.Name) : [],
                CardinalityLimit = 16
            };
        return MetricStreamConfiguration.Drop;
    }

    /// <summary>Limits each instrument to dimensions from its reviewed emitters; values never come from provider text.</summary>
    private static string[] Dimensions(string name)
    {
        return name switch
        {
            "automate.analysis.operations" or "automate.analysis.duration" =>
                ["analysis.operation", "analysis.outcome"],
            "automate.platform.spans.omitted" => ["reason"],
            "automate.security.rate_limit.rejections" => ["security.authentication_state"],
            "telemetry.request.seconds" => ["operation"],
            "automate.deployment.diagnostics.sink.duration" =>
            [
                "deployment.source", "deployment.kind",
                "deployment.severity", "deployment.channel", "deployment.sink"
            ],
            _ when name.StartsWith("automate.deployment.diagnostics.", StringComparison.Ordinal) =>
                ["deployment.source", "deployment.kind", "deployment.severity", "deployment.channel"],
            _ when name.StartsWith("automate.deployment.collector.", StringComparison.Ordinal) => ["deployment.source"],
            _ when name.StartsWith("automate.deployment.jobs.", StringComparison.Ordinal) => ["lane"],
            _ => []
        };
    }

    /// <summary>Retains runtime-owned finite heap and CPU modes, excluding exception classes and external strings.</summary>
    private static string[] RuntimeDimensions(string name)
    {
        return name switch
        {
            "dotnet.gc.collections" or "dotnet.gc.last_collection.heap.size" or
                "dotnet.gc.last_collection.heap.fragmentation.size" => ["gc.heap.generation"],
            "dotnet.process.cpu.time" => ["cpu.mode"],
            _ => []
        };
    }
}