using System.Diagnostics;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.Logging;
using Application.Diagnostics;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Docker;

/// <summary>Delivers each Docker observation live while sampling durable history at a separate cadence.</summary>
internal sealed class DockerMetricDelivery(
    IDeploymentDiagnosticPublisher diagnostics,
    ILogStreamer live,
    IDiagnosticRedactor redactor,
    IDeploymentRuntimeViewers viewers,
    TimeProvider clock,
    int historySampleSeconds,
    ILogger logger)
{
    private DateTimeOffset _nextHistorySample;

    /// <summary>Redacts before live delivery; the bounded publisher handles occasional durable samples separately.</summary>
    internal async Task ObserveAsync(Guid projectId, Guid deploymentId, string container, DockerMetricsLine metrics,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow();
        var observation = new DeploymentDiagnosticEvent(projectId, deploymentId,
            DeploymentDiagnosticSource.DockerContainer, DeploymentDiagnosticKind.Metric,
            DeploymentDiagnosticSeverity.Information, now,
            $"Container metrics: CPU {metrics.Cpu}, memory {metrics.Memory}.",
            new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Metrics, container),
            new Dictionary<string, string> { ["cpu"] = metrics.Cpu, ["memory"] = metrics.Memory },
            SourceIdentity: new DeploymentDiagnosticSourceIdentity(DeploymentDiagnosticComponent.Container,
                DeploymentDiagnosticStream.Metric, container),
            Metrics: DeploymentMetricNormalizer.Docker(metrics.Cpu, metrics.Memory));
        var safe = redactor.Redact(observation).Event;
        if (viewers.HasViewers(projectId, deploymentId))
        {
            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                await live.StreamContainerMetricsAsync(projectId, container, safe.Attributes!["cpu"],
                    safe.Attributes["memory"], token);
                AutoMateTelemetry.EventsDelivered.Add(1, TelemetryTags.Create(safe));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AutoMateTelemetry.DeliveryFailures.Add(1, TelemetryTags.Create(safe));
                logger.LogWarning("Live Docker metric delivery unavailable for project {ProjectId}: {FailureType}.",
                    projectId, exception.GetType().Name);
            }
            finally
            {
                DeploymentDiagnosticPublisher.RecordSinkDuration(safe, "delivery", startedAt);
            }
        }

        if (now < _nextHistorySample) return;
        _nextHistorySample = now.AddSeconds(historySampleSeconds);
        await diagnostics.PublishAsync(safe, token);
    }
}