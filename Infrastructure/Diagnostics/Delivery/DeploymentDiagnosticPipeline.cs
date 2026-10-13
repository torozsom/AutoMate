using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.Logging;
using Application.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

/// <summary>
///     Accepts redacted events into a bounded, non-blocking delivery queue.
/// </summary>
/// <param name="redactor">Applies shared credential and unsafe-text masking.</param>
/// <param name="options">Provides the configured limits and endpoint settings for this service.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
/// <param name="scopes">Provides the service scope factory? boundary used by this operation.</param>
public sealed class DeploymentDiagnosticPublisher(
    IDiagnosticRedactor redactor,
    IOptions<DeploymentDiagnosticOptions> options,
    ILogger<DeploymentDiagnosticPublisher> logger,
    IServiceScopeFactory? scopes = null) : IDeploymentDiagnosticPublisher, IDurableDeploymentDiagnosticPublisher
{
    /// <summary>Serializes admission of new overflow identities to enforce the bookkeeping limit.</summary>
    private readonly object _dropLock = new();

    /// <summary>
    ///     Bounded overflow bookkeeping; metric totals still include omissions beyond this
    ///     capacity.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, int> _droppedProjects = new();

    /// <summary>Bounded channel of normalized events awaiting redaction and durable delivery.</summary>
    private readonly Channel<DeploymentDiagnosticEvent> _events = Channel.CreateBounded<DeploymentDiagnosticEvent>(
        new BoundedChannelOptions(Math.Clamp(options.Value.BufferCapacity, 16, 16_384))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    /// <summary>Reads queued events in their original admission order.</summary>
    internal ChannelReader<DeploymentDiagnosticEvent> Reader => _events.Reader;

    /// <summary>Configured storage deadline shared by durable and queued ingestion.</summary>
    internal TimeSpan PersistenceTimeout => TimeSpan.FromSeconds(options.Value.PersistenceTimeoutSeconds);

    /// <summary>Configured transport deadline shared by live output and safe availability notices.</summary>
    internal TimeSpan DeliveryTimeout => TimeSpan.FromSeconds(options.Value.DeliveryTimeoutSeconds);

    /// <summary>
    ///     Publishes a diagnostic without allowing a slow downstream consumer to block deployment
    ///     work.
    /// </summary>
    /// <param name="diagnosticEvent">Normalized diagnostic to redact before persistence or presentation.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task that completes after the operation finishes.</returns>
    /// <exception cref="OperationCanceledException">The supplied cancellation token is canceled.</exception>
    public ValueTask PublishAsync(DeploymentDiagnosticEvent diagnosticEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(diagnosticEvent);

        using var rootActivity = EnsureTraceContext();
        using var ingestActivity = AutoMateTelemetry.Deployments.StartActivity("deployment.diagnostic.ingest");
        var safeEvent = PrepareEvent(diagnosticEvent);
        var tags = TelemetryTags.Create(safeEvent);
        AutoMateTelemetry.DiagnosticQueueDepth.Add(1);
        if (_events.Writer.TryWrite(safeEvent)) return ValueTask.CompletedTask;
        AutoMateTelemetry.DiagnosticQueueDepth.Add(-1);
        AutoMateTelemetry.EventsDropped.Add(1, tags);
        lock (_dropLock)
        {
            if (_droppedProjects.ContainsKey(safeEvent.ProjectId) || _droppedProjects.Count < 4_096)
                _droppedProjects.AddOrUpdate(safeEvent.ProjectId, 1,
                    (_, count) => count == int.MaxValue ? count : count + 1);
        }

        logger.LogWarning(
            "Dropped deployment diagnostic because the bounded delivery queue is full. Source {Source} kind {Kind} project {ProjectId}.",
            safeEvent.Source, safeEvent.Kind, safeEvent.ProjectId);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     False means no durable receipt exists and the collector must retain its checkpoint.
    /// </summary>
    /// <param name="diagnosticEvent">Normalized diagnostic to redact before persistence or presentation.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning true only after durable acceptance; false requires retaining the
    ///     provider checkpoint.
    /// </returns>
    /// <exception cref="OperationCanceledException">The supplied cancellation token is canceled.</exception>
    public async Task<bool> PublishDurablyAsync(DeploymentDiagnosticEvent diagnosticEvent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(diagnosticEvent);
        using var rootActivity = EnsureTraceContext();
        using var ingestActivity = AutoMateTelemetry.Deployments.StartActivity("deployment.diagnostic.ingest");
        var safe = PrepareEvent(diagnosticEvent) with { EventId = diagnosticEvent.EventId ?? Guid.NewGuid() };
        using var correlation = OperationalLog.BeginCorrelation(logger, safe.DeploymentId, projectId: safe.ProjectId);
        await using var scope = scopes!.CreateAsyncScope();
        var channel = safe.TerminalChannel.Kind == DeploymentTerminalChannelKind.Metrics
            ? null
            : DeploymentDiagnosticDispatcher.GetTerminalChannel(safe);
        long order;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(PersistenceTimeout);
            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                order = await DeploymentTracing.RunAsync(DeploymentOperation.Persistence, safe.ProjectId,
                    safe.DeploymentId,
                    cancellationToken, () => scope.ServiceProvider.GetRequiredService<IDeploymentDiagnosticStore>()
                        .PersistAsync(safe, channel, deadline.Token));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                AutoMateTelemetry.PersistenceFailures.Add(1, TelemetryTags.Create(safe));
                logger.LogWarning("Durable diagnostic storage unavailable. Source {Source} failure {FailureType}.",
                    safe.Source, ex.GetType().Name);
                // No durable confirmation: collectors must retain their checkpoint and retry this event.
                return false;
            }
            finally
            {
                RecordSinkDuration(safe, "persistence", startedAt);
            }
        }

        if (order <= 0) return false;
        AutoMateTelemetry.EventsPersisted.Add(1, TelemetryTags.Create(safe));
        if (channel is not null)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(DeliveryTimeout);
            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                await DeploymentTracing.RunAsync(DeploymentOperation.Delivery, safe.ProjectId, safe.DeploymentId,
                    cancellationToken,
                    () => scope.ServiceProvider.GetRequiredService<ILogStreamer>().StreamTerminalLogAsync(
                        DeploymentTerminalLog.FromEvent(order, safe, channel), deadline.Token));
                AutoMateTelemetry.EventsDelivered.Add(1, TelemetryTags.Create(safe));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                AutoMateTelemetry.DeliveryFailures.Add(1, TelemetryTags.Create(safe));
                logger.LogWarning("Durable diagnostic live delivery unavailable: {FailureType}.", ex.GetType().Name);
            }
            finally
            {
                RecordSinkDuration(safe, "delivery", startedAt);
            }
        }

        return true;
    }

    /// <summary>
    ///     Enriches and redacts both ingestion paths before instrumentation or sink access.
    /// </summary>
    /// <param name="diagnosticEvent">Normalized diagnostic to redact before persistence or presentation.</param>
    /// <returns>The normalized deployment-correlated observation.</returns>
    private DeploymentDiagnosticEvent PrepareEvent(DeploymentDiagnosticEvent diagnosticEvent)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var activity = Activity.Current!;
        var redaction = DeploymentTracing.Run(DeploymentOperation.Redaction, diagnosticEvent.ProjectId,
            diagnosticEvent.DeploymentId,
            () => redactor.Redact(diagnosticEvent with
            {
                TraceId = diagnosticEvent.TraceId ?? activity.TraceId.ToString(),
                SpanId = diagnosticEvent.SpanId ?? activity.SpanId.ToString()
            }));
        var safeEvent = redaction.Event.Message.Length > 4_096
            ? redaction.Event with { Message = redaction.Event.Message[..4_096] + " [output truncated]\r\n" }
            : redaction.Event;
        var tags = TelemetryTags.Create(safeEvent);
        AutoMateTelemetry.EventsReceived.Add(1, tags);
        if (safeEvent.Kind == DeploymentDiagnosticKind.Log)
            AutoMateTelemetry.DiagnosticCursorLag.Record(
                Math.Max(0, (DateTimeOffset.UtcNow - safeEvent.TimestampUtc).TotalSeconds), tags);
        if (redaction.RedactedValueCount > 0)
        {
            AutoMateTelemetry.ValuesRedacted.Add(redaction.RedactedValueCount, tags);
            logger.LogDebug(
                "Redacted {RedactedValueCount} value(s) from deployment diagnostic. Source {Source} kind {Kind} project {ProjectId}.",
                redaction.RedactedValueCount, redaction.Event.Source, redaction.Event.Kind, redaction.Event.ProjectId);
        }

        AutoMateTelemetry.IngestDuration.Record(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, tags);

        return safeEvent;
    }

    /// <summary>
    ///     Consumes a project's pending omission count when the dispatcher makes progress.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <returns>The numeric position, count or status described by this operation.</returns>
    internal int DrainDropped(Guid projectId)
    {
        return _droppedProjects.TryRemove(projectId, out var count) ? count : 0;
    }

    /// <summary>
    ///     Records sink latency using only finite source/channel and operation labels.
    /// </summary>
    /// <param name="diagnosticEvent">Normalized diagnostic to redact before persistence or presentation.</param>
    /// <param name="sink">Finite diagnostic storage or transport sink identifier.</param>
    /// <param name="startedAt">The recorded deployment start time.</param>
    internal static void RecordSinkDuration(DeploymentDiagnosticEvent diagnosticEvent, string sink, long startedAt)
    {
        var tags = TelemetryTags.Create(diagnosticEvent);
        tags.Add("deployment.sink", sink);
        AutoMateTelemetry.SinkDuration.Record(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, tags);
    }

    /// <summary>
    ///     Creates a trace root when a collector has no ambient activity.
    /// </summary>
    /// <returns>
    ///     The activity projection derived from the verified inputs, or null when no value is
    ///     available.
    /// </returns>
    private static Activity? EnsureTraceContext()
    {
        if (Activity.Current is not null) return null;

        return AutoMateTelemetry.Deployments.StartActivity("deployment.diagnostic.ingest")
               ?? new Activity("deployment.diagnostic.ingest").Start();
    }

    /// <summary>
    ///     Rejects malformed observations before any sink receives their contents.
    /// </summary>
    /// <param name="diagnosticEvent">Normalized diagnostic to redact before persistence or presentation.</param>
    /// <exception cref="ArgumentException">A diagnostic event must identify its project.</exception>
    /// <exception cref="ArgumentNullException">A required input or dependency is null.</exception>
    private static void Validate(DeploymentDiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        if (diagnosticEvent.ProjectId == Guid.Empty)
            throw new ArgumentException("A diagnostic event must identify its project.", nameof(diagnosticEvent));
        // Empty output lines are meaningful terminal data; lifecycle/annotation messages are not.
        if (diagnosticEvent.Kind == DeploymentDiagnosticKind.Log)
            ArgumentException.ThrowIfNullOrEmpty(diagnosticEvent.Message);
        else
            ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticEvent.Message);
        ArgumentNullException.ThrowIfNull(diagnosticEvent.TerminalChannel);
        if (diagnosticEvent.Metrics is { } samples && (samples.Count > 3 || samples.Any(s =>
                !MimirDeploymentMetrics.Units.TryGetValue(s.Name, out var unit) || unit != s.Unit ||
                !double.IsFinite(s.Value) || s.Value < 0)))
            throw new ArgumentException("Metric samples must use supported names, finite values and explicit units.",
                nameof(diagnosticEvent));
        if (diagnosticEvent.TerminalChannel.Kind is DeploymentTerminalChannelKind.Container
            or DeploymentTerminalChannelKind.Metrics)
            ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticEvent.TerminalChannel.Target);
    }
}

/// <summary>
///     Drains diagnostics independently from deployment work and forwards only redacted data to
///     presentation.
/// </summary>
/// <param name="publisher">
///     Accepts normalized observations for bounded redaction and durable delivery.
/// </param>
/// <param name="logStreamer">Delivers redacted observations to authorized browser groups.</param>
/// <param name="scopeFactory">Creates independent scopes for concurrent background work.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
public sealed class DeploymentDiagnosticDispatcher(
    DeploymentDiagnosticPublisher publisher,
    ILogStreamer logStreamer,
    IServiceScopeFactory scopeFactory,
    ILogger<DeploymentDiagnosticDispatcher> logger) : BackgroundService
{
    /// <summary>
    ///     Redacts and durably persists queued observations before safe terminal delivery.
    /// </summary>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    /// <returns>A task representing the supervised service lifetime and cleanup.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Deployment diagnostic dispatcher started.");
        await foreach (var diagnosticEvent in publisher.Reader.ReadAllAsync(stoppingToken))
        {
            AutoMateTelemetry.DiagnosticQueueDepth.Add(-1);
            await ProcessAsync(diagnosticEvent, stoppingToken);
            var dropped = publisher.DrainDropped(diagnosticEvent.ProjectId);
            if (dropped == 0) continue;
            var markerChannel = diagnosticEvent.TerminalChannel.Kind == DeploymentTerminalChannelKind.Metrics
                ? new DeploymentTerminalChannel(diagnosticEvent.Source == DeploymentDiagnosticSource.AzureContainerApps
                    ? DeploymentTerminalChannelKind.System
                    : DeploymentTerminalChannelKind.Build)
                : diagnosticEvent.TerminalChannel;
            var marker = diagnosticEvent with
            {
                Kind = DeploymentDiagnosticKind.Annotation,
                Severity = DeploymentDiagnosticSeverity.Warning,
                TimestampUtc = DateTimeOffset.UtcNow,
                Message = $"[{dropped} diagnostic event(s) omitted while the server was busy.]\r\n",
                TerminalChannel = markerChannel,
                Attributes = null,
                Metrics = null,
                EventId = Guid.NewGuid(),
                Cursor = null,
                Sequence = null
            };
            await ProcessAsync(marker, stoppingToken);
        }

        logger.LogInformation("Deployment diagnostic dispatcher stopped.");
    }

    /// <summary>
    ///     Persists before live delivery; local deadlines cannot terminate the host-managed
    ///     dispatcher.
    /// </summary>
    /// <param name="diagnosticEvent">Normalized diagnostic to redact before persistence or presentation.</param>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private async Task ProcessAsync(DeploymentDiagnosticEvent diagnosticEvent, CancellationToken stoppingToken)
    {
        var channel = diagnosticEvent.TerminalChannel.Kind == DeploymentTerminalChannelKind.Metrics
            ? null
            : GetTerminalChannel(diagnosticEvent);
        ActivityContext.TryParse($"00-{diagnosticEvent.TraceId}-{diagnosticEvent.SpanId}-01", null, out var parent);
        using var activity = AutoMateTelemetry.Deployments.StartActivity("deployment.diagnostic.dispatch",
            ActivityKind.Internal, parent);
        using var correlation = OperationalLog.BeginCorrelation(logger, diagnosticEvent.DeploymentId,
            projectId: diagnosticEvent.ProjectId);
        long orderId;
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            deadline.CancelAfter(publisher.PersistenceTimeout);
            await using var scope = scopeFactory.CreateAsyncScope();
            try
            {
                orderId = await DeploymentTracing.RunAsync(DeploymentOperation.Persistence, diagnosticEvent.ProjectId,
                    diagnosticEvent.DeploymentId, stoppingToken, () => scope.ServiceProvider
                        .GetRequiredService<IDeploymentDiagnosticStore>()
                        .PersistAsync(diagnosticEvent, channel, deadline.Token));
            }
            finally
            {
                DeploymentDiagnosticPublisher.RecordSinkDuration(diagnosticEvent, "persistence", startedAt);
            }

            if (orderId == 0) return;
            if (orderId > 0)
                AutoMateTelemetry.EventsPersisted.Add(1, TelemetryTags.Create(diagnosticEvent));
            // Diagnostic payloads belong only in tenant storage and presentation, including legacy delivery modes.
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            AutoMateTelemetry.PersistenceFailures.Add(1, TelemetryTags.Create(diagnosticEvent));
            logger.LogWarning(
                "Failed to persist deployment diagnostic. Source {Source} kind {Kind} failure {FailureType}.",
                diagnosticEvent.Source, diagnosticEvent.Kind, exception.GetType().Name);
            await TryNotifyAsync(diagnosticEvent.ProjectId,
                "Diagnostic storage is temporarily unavailable; some output may be missing.", stoppingToken);
            return;
        }

        // Docker already delivers every observation directly. Do not overwrite a newer live value with a delayed
        // durable sample. Azure metrics retain their provider-polling delivery path.
        if (diagnosticEvent is
            { Source: DeploymentDiagnosticSource.DockerContainer, Kind: DeploymentDiagnosticKind.Metric })
            return;
        startedAt = Stopwatch.GetTimestamp();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            deadline.CancelAfter(publisher.DeliveryTimeout);
            try
            {
                await DeploymentTracing.RunAsync(DeploymentOperation.Delivery, diagnosticEvent.ProjectId,
                    diagnosticEvent.DeploymentId, stoppingToken,
                    () => DeliverToTerminalAsync(diagnosticEvent, channel, orderId, deadline.Token));
            }
            finally
            {
                DeploymentDiagnosticPublisher.RecordSinkDuration(diagnosticEvent, "delivery", startedAt);
            }

            AutoMateTelemetry.EventsDelivered.Add(1, TelemetryTags.Create(diagnosticEvent));
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            AutoMateTelemetry.DeliveryFailures.Add(1, TelemetryTags.Create(diagnosticEvent));
            logger.LogWarning(
                "Failed to deliver deployment diagnostic. Source {Source} kind {Kind} failure {FailureType}.",
                diagnosticEvent.Source, diagnosticEvent.Kind, exception.GetType().Name);
            await TryNotifyAsync(diagnosticEvent.ProjectId,
                "Live diagnostic delivery is temporarily unavailable; reload to recover saved output.", stoppingToken);
        }
    }

    /// <summary>
    ///     Bounds availability notices too, including when the original write already timed out.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="message">The message supplied to the operation.</param>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private async Task TryNotifyAsync(Guid projectId, string message, CancellationToken stoppingToken)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            deadline.CancelAfter(publisher.DeliveryTimeout);
            await logStreamer.StreamTerminalNoticeAsync(projectId, message, deadline.Token);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("Could not deliver diagnostic availability notice. Failure {FailureType}.",
                exception.GetType().Name);
        }
    }

    /// <summary>
    ///     Routes safe data while propagating transport cancellation.
    /// </summary>
    /// <param name="diagnosticEvent">Normalized diagnostic to redact before persistence or presentation.</param>
    /// <param name="channel">The stable terminal channel used for routing.</param>
    /// <param name="orderId">
    ///     The durable ordering position; negative values represent unsaved live observations.
    /// </param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     The diagnostic terminal-channel kind is unsupported.
    /// </exception>
    private Task DeliverToTerminalAsync(DeploymentDiagnosticEvent diagnosticEvent, string? channel, long orderId,
        CancellationToken cancellationToken)
    {
        if (diagnosticEvent is
            {
                Source: DeploymentDiagnosticSource.AzureContainerApps,
                Kind: DeploymentDiagnosticKind.Annotation, Attributes: not null
            } &&
            diagnosticEvent.Attributes.ContainsKey("issue"))
            return logStreamer.StreamTerminalNoticeAsync(diagnosticEvent.ProjectId, diagnosticEvent.Message,
                cancellationToken);
        return diagnosticEvent.TerminalChannel.Kind switch
        {
            DeploymentTerminalChannelKind.Build or DeploymentTerminalChannelKind.System or
                DeploymentTerminalChannelKind.Container => logStreamer.StreamTerminalLogAsync(
                    DeploymentTerminalLog.FromEvent(orderId, diagnosticEvent, channel!), cancellationToken),
            DeploymentTerminalChannelKind.Metrics => DeliverMetricsAsync(diagnosticEvent, cancellationToken),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    /// <summary>
    ///     Delivers typed observations and preserves the original callback for older browser
    ///     clients.
    /// </summary>
    /// <param name="diagnosticEvent">Normalized diagnostic to redact before persistence or presentation.</param>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private async Task DeliverMetricsAsync(DeploymentDiagnosticEvent diagnosticEvent, CancellationToken token)
    {
        if (diagnosticEvent.DeploymentId is { } deployment && diagnosticEvent.Metrics is { Count: > 0 } samples)
            await logStreamer.StreamDeploymentMetricsAsync(new DeploymentRuntimeMetricSnapshot(
                diagnosticEvent.ProjectId,
                deployment, diagnosticEvent.TerminalChannel.Target!,
                diagnosticEvent.SourceIdentity?.Component ?? DeploymentDiagnosticComponent.Container,
                diagnosticEvent.TimestampUtc, samples), token);
        await logStreamer.StreamContainerMetricsAsync(diagnosticEvent.ProjectId,
            diagnosticEvent.TerminalChannel.Target!,
            diagnosticEvent.Attributes?.GetValueOrDefault("cpu") ?? "unknown",
            diagnosticEvent.Attributes?.GetValueOrDefault("memory") ?? "unknown", token);
    }

    /// <summary>
    ///     Maps typed provider-source metadata to the stable browser terminal channel.
    /// </summary>
    /// <param name="diagnosticEvent">Normalized diagnostic to redact before persistence or presentation.</param>
    /// <returns>The normalized identity used for the documented routing or lookup.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     The diagnostic terminal-channel kind is unsupported.
    /// </exception>
    internal static string GetTerminalChannel(DeploymentDiagnosticEvent diagnosticEvent)
    {
        return diagnosticEvent.TerminalChannel.Kind switch
        {
            DeploymentTerminalChannelKind.Build when diagnosticEvent.Source == DeploymentDiagnosticSource.GitHubActions
                =>
                "github-actions",
            DeploymentTerminalChannelKind.Build => "build",
            DeploymentTerminalChannelKind.System when diagnosticEvent.Source ==
                                                      DeploymentDiagnosticSource.AzureContainerApps =>
                "azure-system",
            DeploymentTerminalChannelKind.System => "system",
            DeploymentTerminalChannelKind.Container when diagnosticEvent.TerminalChannel.Target == "cloud-web" =>
                "azure-web",
            DeploymentTerminalChannelKind.Container => diagnosticEvent.TerminalChannel.Target!,
            _ => throw new ArgumentOutOfRangeException()
        };
    }
}

/// <summary>
///     Finite diagnostic metric dimensions, excluding identity and external strings.
/// </summary>
internal static class TelemetryTags
{
    /// <summary>
    ///     Maps undefined numeric enum inputs to a fixed value before metrics observe them.
    /// </summary>
    /// <param name="diagnosticEvent">Normalized diagnostic to redact before persistence or presentation.</param>
    /// <returns>The tag list projection derived from the verified inputs.</returns>
    public static TagList Create(DeploymentDiagnosticEvent diagnosticEvent)
    {
        return new TagList
        {
            { "deployment.source", Finite(diagnosticEvent.Source) },
            { "deployment.kind", Finite(diagnosticEvent.Kind) },
            { "deployment.severity", Finite(diagnosticEvent.Severity) },
            { "deployment.channel", Finite(diagnosticEvent.TerminalChannel.Kind) }
        };
    }

    /// <summary>
    ///     Produces a named value or Unknown, never a numeric string from an untrusted enum cast.
    /// </summary>
    /// <param name="value">The value to normalize, redact or validate.</param>
    /// <typeparam name="T">Type of the operation result.</typeparam>
    /// <returns>The finite derived from the validated input.</returns>
    private static string Finite<T>(T value) where T : struct, Enum
    {
        return Enum.IsDefined(value) ? Enum.GetName(value)! : "Unknown";
    }
}
