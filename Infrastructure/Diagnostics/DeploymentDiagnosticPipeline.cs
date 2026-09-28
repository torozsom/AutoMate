using System.Diagnostics;
using System.Threading.Channels;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.Logging;
using Application.Diagnostics;
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
    private readonly Channel<DeploymentDiagnosticEvent> _events = Channel.CreateBounded<DeploymentDiagnosticEvent>(
        new BoundedChannelOptions(Math.Clamp(options.Value.BufferCapacity, 16, 16_384))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    internal ChannelReader<DeploymentDiagnosticEvent> Reader => _events.Reader;

    /// <inheritdoc />
    public ValueTask PublishAsync(DeploymentDiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(diagnosticEvent);

        using var rootActivity = EnsureTraceContext();
        var activity = Activity.Current!;
        var redaction = redactor.Redact(diagnosticEvent with
        {
            TraceId = diagnosticEvent.TraceId ?? activity.TraceId.ToString(),
            SpanId = diagnosticEvent.SpanId ?? activity.SpanId.ToString()
        });
        var tags = TelemetryTags.Create(redaction.Event);
        AutoMateTelemetry.EventsReceived.Add(1, tags);
        if (redaction.RedactedValueCount > 0)
            AutoMateTelemetry.ValuesRedacted.Add(redaction.RedactedValueCount, tags);

        if (_events.Writer.TryWrite(redaction.Event)) return ValueTask.CompletedTask;

        AutoMateTelemetry.EventsDropped.Add(1, tags);
        logger.LogWarning("Dropped deployment diagnostic because the bounded delivery queue is full. Source {Source} kind {Kind} project {ProjectId}.",
            redaction.Event.Source, redaction.Event.Kind, redaction.Event.ProjectId);
        return ValueTask.CompletedTask;
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
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticEvent.Message);
        ArgumentNullException.ThrowIfNull(diagnosticEvent.TerminalChannel);
        if (diagnosticEvent.TerminalChannel.Kind is DeploymentTerminalChannelKind.Container or DeploymentTerminalChannelKind.Metrics)
            ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticEvent.TerminalChannel.Target);
    }
}

/// <summary>Drains diagnostics independently from deployment work and forwards only redacted data to presentation.</summary>
public sealed class DeploymentDiagnosticDispatcher(
    DeploymentDiagnosticPublisher publisher,
    ILogStreamer logStreamer,
    ILogger<DeploymentDiagnosticDispatcher> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var diagnosticEvent in publisher.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var activity = StartDeliveryActivity(diagnosticEvent);
                using var scope = logger.BeginScope(new Dictionary<string, object?>
                {
                    ["ProjectId"] = diagnosticEvent.ProjectId,
                    ["DeploymentId"] = diagnosticEvent.DeploymentId,
                    ["DiagnosticSource"] = diagnosticEvent.Source,
                    ["DiagnosticKind"] = diagnosticEvent.Kind,
                    ["TraceId"] = diagnosticEvent.TraceId
                });

                logger.Log(MapLogLevel(diagnosticEvent.Severity), "Deployment diagnostic: {DiagnosticMessage}", diagnosticEvent.Message);
                await DeliverAsync(diagnosticEvent);
                AutoMateTelemetry.EventsDelivered.Add(1, TelemetryTags.Create(diagnosticEvent));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AutoMateTelemetry.DeliveryFailures.Add(1, TelemetryTags.Create(diagnosticEvent));
                logger.LogWarning(ex, "Failed to deliver deployment diagnostic. Source {Source} kind {Kind} project {ProjectId}.",
                    diagnosticEvent.Source, diagnosticEvent.Kind, diagnosticEvent.ProjectId);
            }
        }
    }

    private Task DeliverAsync(DeploymentDiagnosticEvent diagnosticEvent)
    {
        return diagnosticEvent.TerminalChannel.Kind switch
        {
            DeploymentTerminalChannelKind.Build or DeploymentTerminalChannelKind.System =>
                logStreamer.StreamBuildLogsAsync(diagnosticEvent.ProjectId, diagnosticEvent.Message),
            DeploymentTerminalChannelKind.Container => logStreamer.StreamContainerLogsAsync(diagnosticEvent.ProjectId,
                diagnosticEvent.TerminalChannel.Target!, diagnosticEvent.Message),
            DeploymentTerminalChannelKind.Metrics => logStreamer.StreamContainerMetricsAsync(diagnosticEvent.ProjectId,
                diagnosticEvent.TerminalChannel.Target!,
                diagnosticEvent.Attributes?.GetValueOrDefault("cpu") ?? "unknown",
                diagnosticEvent.Attributes?.GetValueOrDefault("memory") ?? "unknown"),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private static Activity? StartDeliveryActivity(DeploymentDiagnosticEvent diagnosticEvent)
    {
        if (!string.IsNullOrWhiteSpace(diagnosticEvent.TraceId) &&
            !string.IsNullOrWhiteSpace(diagnosticEvent.SpanId) &&
            ActivityContext.TryParse($"00-{diagnosticEvent.TraceId}-{diagnosticEvent.SpanId}-01", null, false,
                out var parentContext))
            return AutoMateTelemetry.Deployments.StartActivity("deployment.diagnostic.deliver", ActivityKind.Internal,
                parentContext);

        return AutoMateTelemetry.Deployments.StartActivity("deployment.diagnostic.deliver");
    }

    private static LogLevel MapLogLevel(DeploymentDiagnosticSeverity severity) => severity switch
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

internal static class TelemetryTags
{
    public static TagList Create(DeploymentDiagnosticEvent diagnosticEvent) => new()
    {
        { "deployment.source", diagnosticEvent.Source.ToString() },
        { "deployment.kind", diagnosticEvent.Kind.ToString() },
        { "deployment.severity", diagnosticEvent.Severity.ToString() },
        { "deployment.channel", diagnosticEvent.TerminalChannel.Kind.ToString() }
    };
}
