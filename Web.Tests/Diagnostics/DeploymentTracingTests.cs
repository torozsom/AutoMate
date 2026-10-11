using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Application.Diagnostics;
using Infrastructure.Diagnostics;
using Infrastructure.Observability;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Web.Observability;
using Xunit;

namespace Web.Tests;

/// <summary>Checks real SDK span export, parentage and privacy without contacting a collector.</summary>
public sealed class DeploymentTracingTests
{
    /// <summary>Analysis SDK export retains reviewed names, parentage and GUID correlation while removing injected payloads.</summary>
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
            await AnalysisTelemetry.RunAsync(AnalysisOperation.Provider, deployment, analysis, CancellationToken.None,
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
            .Where(span => Equals(span.Tags.GetValueOrDefault("deployment.analysis.id"), analysis)).ToArray();
        Assert.Equal(2, spans.Length);
        var root = Assert.Single(spans, span => span.Name == "analysis.process");
        var child = Assert.Single(spans, span => span.Name == "analysis.provider");
        Assert.Equal(root.SpanId, child.ParentSpanId);
        Assert.Equal("completed", child.Tags["analysis.outcome"]);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(spans));
    }

    /// <summary>Provider calls retain their result/exception/cancellation semantics and export only correlation/outcomes.</summary>
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
        var action = DeploymentTracing.RunAsync(DeploymentOperation.AzureLogs, project, deployment, canceled.Token,
            () =>
            {
                if (scenario == "failure") return Task.FromException<string>(failure);
                if (scenario == "canceled")
                {
                    canceled.Cancel();
                    return Task.FromCanceled<string>(canceled.Token);
                }

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

/// <summary>Copies activities at the export boundary before provider disposal.</summary>
internal sealed class TraceSnapshotExporter : BaseExporter<Activity>
{
    /// <summary>Detached immutable span snapshots.</summary>
    internal ConcurrentQueue<ExportedSpan> Spans { get; } = new();

    /// <inheritdoc />
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

/// <summary>Safe span snapshot used for correlation/status/privacy assertions.</summary>
internal sealed record ExportedSpan(
    string Name,
    ActivityTraceId TraceId,
    ActivitySpanId SpanId,
    ActivitySpanId ParentSpanId,
    ActivityStatusCode Status,
    string? Description,
    IReadOnlyDictionary<string, object?> Tags,
    int EventCount);