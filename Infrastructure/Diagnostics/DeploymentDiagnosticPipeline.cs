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

/// <summary>Accepts redacted events into a bounded, non-blocking delivery queue.</summary>
public sealed class DeploymentDiagnosticPublisher(
    IDiagnosticRedactor redactor,
    IOptions<DeploymentDiagnosticOptions> options,
    ILogger<DeploymentDiagnosticPublisher> logger) : IDeploymentDiagnosticPublisher
{
    private readonly ConcurrentDictionary<Guid, int> _droppedProjects = new();

    private readonly Channel<DeploymentDiagnosticEvent> _events = Channel.CreateBounded<DeploymentDiagnosticEvent>(
        new BoundedChannelOptions(Math.Clamp(options.Value.BufferCapacity, 16, 16_384))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    internal ChannelReader<DeploymentDiagnosticEvent> Reader => _events.Reader;

    /// <inheritdoc />
    public ValueTask PublishAsync(DeploymentDiagnosticEvent diagnosticEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(diagnosticEvent);

        using var rootActivity = EnsureTraceContext();
        using var ingestActivity = AutoMateTelemetry.Deployments.StartActivity("deployment.diagnostic.ingest");
        var startedAt = Stopwatch.GetTimestamp();
        var activity = Activity.Current!;
        var redaction = redactor.Redact(diagnosticEvent with
        {
            TraceId = diagnosticEvent.TraceId ?? activity.TraceId.ToString(),
            SpanId = diagnosticEvent.SpanId ?? activity.SpanId.ToString()
        });
        var safeEvent = redaction.Event.Message.Length > 4_096
            ? redaction.Event with { Message = redaction.Event.Message[..4_096] + " [output truncated]\r\n" }
            : redaction.Event;
        var tags = TelemetryTags.Create(safeEvent);
        AutoMateTelemetry.EventsReceived.Add(1, tags);
        if (redaction.RedactedValueCount > 0)
        {
            AutoMateTelemetry.ValuesRedacted.Add(redaction.RedactedValueCount, tags);
            logger.LogDebug(
                "Redacted {RedactedValueCount} value(s) from deployment diagnostic. Source {Source} kind {Kind} project {ProjectId}.",
                redaction.RedactedValueCount, redaction.Event.Source, redaction.Event.Kind, redaction.Event.ProjectId);
        }

        AutoMateTelemetry.IngestDuration.Record(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, tags);

        if (_events.Writer.TryWrite(safeEvent)) return ValueTask.CompletedTask;

        AutoMateTelemetry.EventsDropped.Add(1, tags);
        _droppedProjects.AddOrUpdate(safeEvent.ProjectId, 1, (_, count) => count == int.MaxValue ? count : count + 1);
        logger.LogWarning(
            "Dropped deployment diagnostic because the bounded delivery queue is full. Source {Source} kind {Kind} project {ProjectId}.",
            safeEvent.Source, safeEvent.Kind, safeEvent.ProjectId);
        return ValueTask.CompletedTask;
    }

    internal int DrainDropped(Guid projectId)
    {
        return _droppedProjects.TryRemove(projectId, out var count) ? count : 0;
    }

    private static Activity? EnsureTraceContext()
    {
        if (Activity.Current is not null) return null;

        return AutoMateTelemetry.Deployments.StartActivity("deployment.diagnostic.ingest")
               ?? new Activity("deployment.diagnostic.ingest").Start();
    }

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

/// <summary>Drains diagnostics independently from deployment work and forwards only redacted data to presentation.</summary>
public sealed class DeploymentDiagnosticDispatcher(
    DeploymentDiagnosticPublisher publisher,
    ILogStreamer logStreamer,
    IServiceScopeFactory scopeFactory,
    ILogger<DeploymentDiagnosticDispatcher> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Deployment diagnostic dispatcher started.");
        await foreach (var diagnosticEvent in publisher.Reader.ReadAllAsync(stoppingToken))
        {
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
                Attributes = null
            };
            await ProcessAsync(marker, stoppingToken);
        }

        logger.LogInformation("Deployment diagnostic dispatcher stopped.");
    }

    private async Task ProcessAsync(DeploymentDiagnosticEvent diagnosticEvent, CancellationToken stoppingToken)
    {
        var channel = diagnosticEvent.TerminalChannel.Kind == DeploymentTerminalChannelKind.Metrics
            ? null
            : GetTerminalChannel(diagnosticEvent);
        long orderId;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            orderId = await scope.ServiceProvider.GetRequiredService<IDeploymentDiagnosticStore>()
                .PersistAsync(diagnosticEvent, channel, stoppingToken);
            if (orderId == 0) return;
            if (orderId > 0)
            {
                AutoMateTelemetry.EventsPersisted.Add(1, TelemetryTags.Create(diagnosticEvent));
                LogDiagnostic(diagnosticEvent);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AutoMateTelemetry.PersistenceFailures.Add(1, TelemetryTags.Create(diagnosticEvent));
            logger.LogWarning(exception,
                "Failed to persist deployment diagnostic. Source {Source} kind {Kind} project {ProjectId}.",
                diagnosticEvent.Source, diagnosticEvent.Kind, diagnosticEvent.ProjectId);
            await TryNotifyAsync(diagnosticEvent.ProjectId,
                "Diagnostic storage is temporarily unavailable; some output may be missing.");
            return;
        }

        // Docker already delivers every observation directly. Do not overwrite a newer live value with a delayed
        // durable sample. Azure metrics retain their provider-polling delivery path.
        if (diagnosticEvent is
            { Source: DeploymentDiagnosticSource.DockerContainer, Kind: DeploymentDiagnosticKind.Metric })
            return;
        try
        {
            await DeliverToTerminalAsync(diagnosticEvent, channel, orderId);
            AutoMateTelemetry.EventsDelivered.Add(1, TelemetryTags.Create(diagnosticEvent));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AutoMateTelemetry.DeliveryFailures.Add(1, TelemetryTags.Create(diagnosticEvent));
            logger.LogWarning(exception,
                "Failed to deliver deployment diagnostic. Source {Source} kind {Kind} project {ProjectId}.",
                diagnosticEvent.Source, diagnosticEvent.Kind, diagnosticEvent.ProjectId);
            await TryNotifyAsync(diagnosticEvent.ProjectId,
                "Live diagnostic delivery is temporarily unavailable; reload to recover saved output.");
        }
    }

    private async Task TryNotifyAsync(Guid projectId, string message)
    {
        try
        {
            await logStreamer.StreamTerminalNoticeAsync(projectId, message);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not deliver diagnostic availability notice for project {ProjectId}.",
                projectId);
        }
    }

    private Task DeliverToTerminalAsync(DeploymentDiagnosticEvent diagnosticEvent, string? channel, long orderId)
    {
        return diagnosticEvent.TerminalChannel.Kind switch
        {
            DeploymentTerminalChannelKind.Build or DeploymentTerminalChannelKind.System or
                DeploymentTerminalChannelKind.Container => logStreamer.StreamTerminalLogAsync(
                    new DeploymentTerminalLog(orderId, diagnosticEvent.ProjectId, diagnosticEvent.DeploymentId,
                        channel!, diagnosticEvent.Message)),
            DeploymentTerminalChannelKind.Metrics => logStreamer.StreamContainerMetricsAsync(diagnosticEvent.ProjectId,
                diagnosticEvent.TerminalChannel.Target!,
                diagnosticEvent.Attributes?.GetValueOrDefault("cpu") ?? "unknown",
                diagnosticEvent.Attributes?.GetValueOrDefault("memory") ?? "unknown"),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private static string GetTerminalChannel(DeploymentDiagnosticEvent diagnosticEvent)
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

    private void LogDiagnostic(DeploymentDiagnosticEvent diagnosticEvent)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ProjectId"] = diagnosticEvent.ProjectId,
            ["DeploymentId"] = diagnosticEvent.DeploymentId,
            ["DiagnosticSource"] = diagnosticEvent.Source,
            ["DiagnosticComponent"] = diagnosticEvent.SourceIdentity?.Component,
            ["DiagnosticKind"] = diagnosticEvent.Kind,
            ["TraceId"] = diagnosticEvent.TraceId
        });
        logger.Log(MapLogLevel(diagnosticEvent.Severity), "Deployment diagnostic: {DiagnosticMessage}",
            diagnosticEvent.Message);
    }

    private static LogLevel MapLogLevel(DeploymentDiagnosticSeverity severity)
    {
        return severity switch
        {
            DeploymentDiagnosticSeverity.Trace => LogLevel.Trace,
            DeploymentDiagnosticSeverity.Debug => LogLevel.Debug,
            DeploymentDiagnosticSeverity.Information => LogLevel.Information,
            DeploymentDiagnosticSeverity.Warning => LogLevel.Warning,
            DeploymentDiagnosticSeverity.Error => LogLevel.Error,
            DeploymentDiagnosticSeverity.Critical => LogLevel.Critical,
            _ => LogLevel.Information
        };
    }
}

internal static class TelemetryTags
{
    public static TagList Create(DeploymentDiagnosticEvent diagnosticEvent)
    {
        return new TagList
        {
            { "deployment.source", diagnosticEvent.Source.ToString() },
            { "deployment.kind", diagnosticEvent.Kind.ToString() },
            { "deployment.severity", diagnosticEvent.Severity.ToString() },
            { "deployment.channel", diagnosticEvent.TerminalChannel.Kind.ToString() }
        };
    }
}