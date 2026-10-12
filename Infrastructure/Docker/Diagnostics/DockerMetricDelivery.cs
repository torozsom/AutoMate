using System.Diagnostics;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.Logging;
using Application.Diagnostics;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Docker;

/// <summary>
///     Delivers each Docker observation live while sampling durable history at a separate
///     cadence.
/// </summary>
/// <param name="diagnostics">Accepts normalized observations for redaction and durable delivery.</param>
/// <param name="live">Delivers redacted observations to authorized browser groups.</param>
/// <param name="redactor">Applies shared credential and unsafe-text masking.</param>
/// <param name="viewers">Provides the deployment runtime viewers boundary used by this operation.</param>
/// <param name="clock">Time source used for deterministic sampling and deadlines.</param>
/// <param name="historySampleSeconds">Minimum cadence for persisting numeric history samples.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
internal sealed class DockerMetricDelivery(
    IDeploymentDiagnosticPublisher diagnostics,
    ILogStreamer live,
    IDiagnosticRedactor redactor,
    IDeploymentRuntimeViewers viewers,
    TimeProvider clock,
    int historySampleSeconds,
    ILogger logger)
{
    /// <summary>Earliest next durable metric sample time; live delivery remains independent.</summary>
    private DateTimeOffset _nextHistorySample;

    /// <summary>
    ///     Redacts before live delivery; the bounded publisher handles occasional durable samples
    ///     separately.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">
    ///     The deployment identity; null is used only where legacy observations permit it.
    /// </param>
    /// <param name="container">Verified owned container identity used for observation routing.</param>
    /// <param name="metrics">Recorded numeric samples or aggregate points.</param>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    /// <exception cref="OperationCanceledException">The supplied cancellation token is canceled.</exception>
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
        // Legacy viewing leases do not gate collection or delivery; SignalR group membership remains authorized.
        _ = viewers;
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

        if (now < _nextHistorySample) return;
        _nextHistorySample = now.AddSeconds(historySampleSeconds);
        await diagnostics.PublishAsync(safe, token);
    }
}