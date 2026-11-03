using System.Diagnostics;
using Application.Abstractions.Diagnostics.Delivery;
using Application.Abstractions.Diagnostics.Events;
using Application.Abstractions.Diagnostics.Metrics;
using Application.Abstractions.Diagnostics.Redaction;
using Application.Abstractions.Logging;
using Application.Diagnostics;
using Infrastructure.Diagnostics.Delivery;
using Microsoft.Extensions.Logging;
using DeploymentMetricNormalizer = Infrastructure.Diagnostics.Metrics.DeploymentMetricNormalizer;

namespace Infrastructure.Docker.Diagnostics;

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
/// <param name="liveMetricIntervalMilliseconds">Minimum callback-pair interval for this container.</param>
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
    ILogger logger,
    int liveMetricIntervalMilliseconds = 1000) : IAsyncDisposable
{
    /// <summary>Collector-owned cancellation and single latest-value slot.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Guards use after collector disposal.</summary>
    private bool _disposed;

    /// <summary>Owned trailing delivery task, started after the first immediate update.</summary>
    private Task? _loop;

    /// <summary>Earliest next durable metric sample time; live delivery remains independent.</summary>
    private DateTimeOffset _nextHistorySample;

    /// <summary>Last redacted observation awaiting its UI interval.</summary>
    private DeploymentDiagnosticEvent? _pending;

    /// <summary>Links producer shutdown to pending callbacks.</summary>
    private CancellationTokenRegistration _registration;

    /// <summary>
    ///     Cancels and observes trailing work; pending UI state is discarded on collector retirement.
    /// </summary>
    /// <returns>A task completing after no further callbacks can be emitted.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _registration.DisposeAsync();
        await _lifetime.CancelAsync();
        if (_loop is not null) await _loop;
        Interlocked.Exchange(ref _pending, null);
        _lifetime.Dispose();
    }

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
        ObjectDisposedException.ThrowIf(_disposed, this);
        OperationalMetrics.LiveObservations.Add(1);
        if (_loop is null)
        {
            _registration = token.Register(() => _lifetime.Cancel());
            await DeliverAsync(safe, _lifetime.Token);
            _loop = RunAsync(_lifetime.Token);
        }
        else if (Interlocked.Exchange(ref _pending, safe) is not null)
        {
            OperationalMetrics.Coalesced.Add(1);
        }

        if (now < _nextHistorySample) return;
        _nextHistorySample = now.AddSeconds(historySampleSeconds);
        OperationalMetrics.HistoryAdmissions.Add(1);
        await diagnostics.PublishAsync(safe, token);
    }

    /// <summary>
    ///     Flushes at most one latest observation per interval without delaying history admission.
    /// </summary>
    /// <param name="token">Collector lifetime.</param>
    /// <returns>The owned coalescer lifetime.</returns>
    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(liveMetricIntervalMilliseconds), clock, token);
                if (Interlocked.Exchange(ref _pending, null) is { } observation)
                    await DeliverAsync(observation, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    ///     Attempts both compatibility callbacks independently, counting a delivered observation once.
    /// </summary>
    /// <param name="safe">Latest centrally redacted source observation.</param>
    /// <param name="token">Collector lifetime preventing post-retirement writes.</param>
    /// <returns>A task completing after both callback attempts.</returns>
    private async Task DeliverAsync(DeploymentDiagnosticEvent safe, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        var typed = true;
        if (safe.Metrics is { Count: > 0 } samples)
            typed = await CallbackAsync("typed_metrics", () => live.StreamDeploymentMetricsAsync(
                new DeploymentRuntimeMetricSnapshot(safe.ProjectId, safe.DeploymentId!.Value,
                    safe.TerminalChannel.Target!,
                    safe.SourceIdentity?.Component ?? DeploymentDiagnosticComponent.Container,
                    safe.TimestampUtc, samples), token), safe, token);
        var legacy = await CallbackAsync("legacy_metrics", () => live.StreamContainerMetricsAsync(safe.ProjectId,
            safe.TerminalChannel.Target!, safe.Attributes!["cpu"], safe.Attributes["memory"], token), safe, token);
        if (typed && legacy) AutoMateTelemetry.EventsDelivered.Add(1, TelemetryTags.Create(safe));
        else AutoMateTelemetry.DeliveryFailures.Add(1, TelemetryTags.Create(safe));
        DeploymentDiagnosticPublisher.RecordSinkDuration(safe, "delivery", started);
    }

    /// <summary>
    ///     Isolates a callback failure from its compatibility partner and durable history.
    /// </summary>
    /// <param name="stage">Fixed callback family.</param>
    /// <param name="send">Safe transport operation.</param>
    /// <param name="safe">Correlation metadata.</param>
    /// <param name="token">Collector cancellation.</param>
    /// <returns>Whether transport accepted the callback.</returns>
    private async Task<bool> CallbackAsync(string stage, Func<Task> send, DeploymentDiagnosticEvent safe,
        CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "completed";
        try
        {
            token.ThrowIfCancellationRequested();
            await send();
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            outcome = "canceled";
            throw;
        }
        catch (Exception ex)
        {
            outcome = "failed";
            logger.LogWarning("Live Docker metric delivery unavailable for project {ProjectId}: {FailureType}.",
                safe.ProjectId, ex.GetType().Name);
            return false;
        }
        finally
        {
            OperationalMetrics.Record("DockerContainer", stage, outcome, started);
        }
    }
}
