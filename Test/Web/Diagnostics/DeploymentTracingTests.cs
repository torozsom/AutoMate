using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Application.Diagnostics;
using Infrastructure.Diagnostics.Metrics;
using Infrastructure.Observability;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Web.Observability;
using DiagnosticRedactor = Infrastructure.Diagnostics.Redaction.DiagnosticRedactor;

namespace Test.Web.Diagnostics;

/// <summary>
///     Checks real SDK span export, parentage and privacy without contacting a collector.
/// </summary>
public sealed class DeploymentTracingTests
{
    /// <summary>
    ///     Dashboard-facing instrument names, units and observable kinds remain stable without introducing identity
    ///     labels.
    /// </summary>
    [Fact]
    public void Cloud_and_storage_instrument_schema_remains_stable()
    {
        (System.Diagnostics.Metrics.ObservableGauge<long> Instrument, string Name, string? Unit)[] gauges =
        [
            (AutoMateTelemetry.CloudQueued, "automate.cloud.queue.depth", null),
            (AutoMateTelemetry.CloudActive, "automate.cloud.launches.active", null),
            (AutoMateTelemetry.CloudWebhookBacklog, "automate.cloud.webhook.backlog", null),
            (AutoMateTelemetry.CloudReconciliationBacklog, "automate.cloud.reconciliation.backlog", null),
            (AutoMateTelemetry.CloudOldestQueueAge, "automate.cloud.queue.oldest_age", "ms")
        ];
        foreach (var gauge in gauges)
        {
            Assert.Equal(gauge.Name, gauge.Instrument.Name);
            Assert.Equal(gauge.Unit, gauge.Instrument.Unit);
            Assert.True(gauge.Instrument.IsObservable);
        }

        Assert.Equal("telemetry.ingested.bytes", TelemetryStorageMetrics.IngestedBytes.Name);
        Assert.False(TelemetryStorageMetrics.IngestedBytes.IsObservable);
    }

    /// <summary>
    ///     Analysis SDK export retains reviewed names, parentage and GUID correlation while removing
    ///     injected payloads.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Analysis_spans_cross_export_policy_without_payload_or_metric_identifiers()
    {
        using var exporter = new TraceSnapshotExporter();
        var policy = new PlatformTelemetryPolicy(new DiagnosticRedactor());

        using var provider = Sdk.CreateTracerProviderBuilder().AddSource("AutoMate.Analysis")
            .AddProcessor(new SafeTraceProcessor(policy))
            .AddProcessor(new SimpleActivityExportProcessor(exporter)).Build();

        var deployment = Guid.NewGuid();
        var analysis = Guid.NewGuid();

        using (var parent = AnalysisTelemetry.Start(AnalysisOperation.Process, deployment, analysis))
        {
            await AnalysisTelemetry.RunAsync(AnalysisOperation.Provider, deployment, analysis,
                () =>
                {
                    Activity.Current!.SetTag("provider.body", "private-provider-text");
                    Activity.Current.SetTag("analysis.outcome", "private-outcome");
                    Activity.Current.AddBaggage("private-baggage", "private-token");

                    return Task.FromResult(true);
                });
            parent.Finish(AnalysisOutcome.Completed);
        }

        var spans = exporter.Spans
            .Where(span => Equals(span.Tags.GetValueOrDefault("deployment.analysis.id"), analysis))
            .ToArray();

        Assert.Equal(2, spans.Length);

        var root = Assert.Single(spans, span => span.Name == "analysis.process");
        var child = Assert.Single(spans, span => span.Name == "analysis.provider");

        Assert.Equal(root.SpanId, child.ParentSpanId);
        Assert.Equal("completed", child.Tags["analysis.outcome"]);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(spans));
    }

    /// <summary>
    ///     Provider calls retain their result/exception/cancellation semantics and export only
    ///     correlation/outcomes.
    /// </summary>
    /// <param name="scenario">Selected fixture behavior scenario.</param>
    /// <param name="outcome">Configured or expected operation outcome.</param>
    /// <param name="status">Expected or synthetic trace status.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData("success", "completed", ActivityStatusCode.Ok)]
    [InlineData("failure", "failed", ActivityStatusCode.Error)]
    [InlineData("canceled", "canceled", ActivityStatusCode.Unset)]
    [InlineData("returned-failure", "failed", ActivityStatusCode.Error)]
    public async Task Provider_spans_preserve_results_and_parentage_without_payloads(string scenario, string outcome,
        ActivityStatusCode status)
    {
        using var exporter = new TraceSnapshotExporter();
        using var provider = Sdk.CreateTracerProviderBuilder().AddSource(AutoMateTelemetry.Deployments.Name)
            .AddProcessor(new SimpleActivityExportProcessor(exporter)).Build();
        var project = Guid.NewGuid();
        var deployment = Guid.NewGuid();
        using var root = new Activity("fixture").SetIdFormat(ActivityIdFormat.W3C).Start();

        root.ActivityTraceFlags = ActivityTraceFlags.Recorded;
        using var canceled = new CancellationTokenSource();
        var failure = new InvalidOperationException("password=private-provider-body");
        if (scenario == "canceled") await canceled.CancelAsync();
        var cancellationToken = canceled.Token;
        var action = DeploymentTracing.RunAsync(DeploymentOperation.AzureLogs, project, deployment, canceled.Token,
            () =>
            {
                if (scenario == "failure") return Task.FromException<string>(failure);
                if (scenario == "canceled") return Task.FromCanceled<string>(cancellationToken);

                return Task.FromResult("private-result-body");
            }, _ => scenario != "returned-failure");

        if (scenario == "failure")
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => action));
        else if (scenario == "canceled") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        else Assert.Equal("private-result-body", await action);

        var span = Assert.Single(exporter.Spans,
            span => Equals(span.Tags.GetValueOrDefault("deployment.id"), deployment));
        Assert.Equal(root.TraceId, span.TraceId);
        Assert.Equal(root.SpanId, span.ParentSpanId);
        Assert.Equal("azure.logs.query", span.Name);
        Assert.Equal(project, span.Tags["deployment.project.id"]);
        Assert.Equal(outcome, span.Tags["deployment.outcome"]);
        Assert.Equal(status, span.Status);
        Assert.Null(span.Description);
        Assert.Equal(0, span.EventCount);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(span));
        Assert.Same(root, Activity.Current);
    }
}

/// <summary>
///     Copies activities at the export boundary before provider disposal.
/// </summary>
internal sealed class TraceSnapshotExporter : BaseExporter<Activity>
{
    /// <summary>Detached immutable span snapshots.</summary>
    internal ConcurrentQueue<ExportedSpan> Spans { get; } = new();

    /// <summary>
    ///     Captures detached span metadata for assertions and reports a successful export.
    /// </summary>
    /// <param name="batch">Exported SDK record batch or batch-export variant.</param>
    /// <returns>The export outcome after the fixture captures its SDK records.</returns>
    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch)
            Spans.Enqueue(new ExportedSpan(activity.OperationName, activity.TraceId, activity.SpanId,
                activity.ParentSpanId,
                activity.Status, activity.StatusDescription,
                activity.TagObjects.ToDictionary(pair => pair.Key, pair => pair.Value),
                activity.Events.Count()));

        return ExportResult.Success;
    }
}

/// <summary>
///     Safe span snapshot used for correlation/status/privacy assertions.
/// </summary>
/// <param name="Name">Fixture member, option or metric name selected by the caller.</param>
/// <param name="TraceId">Captured trace identity.</param>
/// <param name="SpanId">The optional diagnostic trace span identifier.</param>
/// <param name="ParentSpanId">Captured parent span identity.</param>
/// <param name="Status">Expected or synthetic trace status.</param>
/// <param name="Description">Captured optional span status description.</param>
/// <param name="Tags">Detached captured SDK attributes.</param>
/// <param name="EventCount">Captured span event count.</param>
internal sealed record ExportedSpan(
    string Name,
    ActivityTraceId TraceId,
    ActivitySpanId SpanId,
    ActivitySpanId ParentSpanId,
    ActivityStatusCode Status,
    string? Description,
    IReadOnlyDictionary<string, object?> Tags,
    int EventCount);
