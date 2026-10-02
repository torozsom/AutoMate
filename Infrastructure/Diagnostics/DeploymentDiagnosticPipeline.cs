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
        var tags = TelemetryTags.Create(redaction.Event);
        AutoMateTelemetry.EventsReceived.Add(1, tags);
        if (redaction.RedactedValueCount > 0)
        {
            AutoMateTelemetry.ValuesRedacted.Add(redaction.RedactedValueCount, tags);
            logger.LogDebug(
                "Redacted {RedactedValueCount} value(s) from deployment diagnostic. Source {Source} kind {Kind} project {ProjectId}.",
                redaction.RedactedValueCount, redaction.Event.Source, redaction.Event.Kind, redaction.Event.ProjectId);
        }

        AutoMateTelemetry.IngestDuration.Record(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, tags);

        if (_events.Writer.TryWrite(redaction.Event)) return ValueTask.CompletedTask;

        AutoMateTelemetry.EventsDropped.Add(1, tags);
        logger.LogWarning(
            "Dropped deployment diagnostic because the bounded delivery queue is full. Source {Source} kind {Kind} project {ProjectId}.",
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
    IOptions<DeploymentDiagnosticOptions> options,
    ILogger<DeploymentDiagnosticDispatcher> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Deployment diagnostic dispatcher started.");
        var capacity = Math.Clamp(options.Value.BufferCapacity, 16, 16_384);
        var persistenceEvents = CreateSinkChannel(capacity);
        var terminalEvents = CreateSinkChannel(capacity);
        var persistenceWorker = PersistAsync(persistenceEvents.Reader, stoppingToken);
        var terminalWorker = DeliverAsync(terminalEvents.Reader, stoppingToken);

        try
        {
            await foreach (var diagnosticEvent in publisher.Reader.ReadAllAsync(stoppingToken))
            {
                LogDiagnostic(diagnosticEvent);
                TryWrite(persistenceEvents.Writer, diagnosticEvent, "persistence");
                TryWrite(terminalEvents.Writer, diagnosticEvent, "terminal delivery");
            }
        }
        finally
        {
            persistenceEvents.Writer.TryComplete();
            terminalEvents.Writer.TryComplete();
            await Task.WhenAll(persistenceWorker, terminalWorker);
            logger.LogInformation("Deployment diagnostic dispatcher stopped.");
        }
    }

    private async Task PersistAsync(ChannelReader<DeploymentDiagnosticEvent> events,
        CancellationToken cancellationToken)
    {
        await foreach (var diagnosticEvent in events.ReadAllAsync(cancellationToken))
            try
            {
                using var activity = StartOperationActivity("deployment.diagnostic.persist", diagnosticEvent);
                var startedAt = Stopwatch.GetTimestamp();
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IDeploymentDiagnosticStore>()
                    .PersistAsync(diagnosticEvent, cancellationToken);
                AutoMateTelemetry.EventsPersisted.Add(1, TelemetryTags.Create(diagnosticEvent));
                var tags = TelemetryTags.Create(diagnosticEvent);
                tags.Add("deployment.sink", "persistence");
                AutoMateTelemetry.SinkDuration.Record(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, tags);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AutoMateTelemetry.PersistenceFailures.Add(1, TelemetryTags.Create(diagnosticEvent));
                logger.LogWarning(exception,
                    "Failed to persist deployment diagnostic. Source {Source} kind {Kind} project {ProjectId}.",
                    diagnosticEvent.Source, diagnosticEvent.Kind, diagnosticEvent.ProjectId);
            }
    }

    private async Task DeliverAsync(ChannelReader<DeploymentDiagnosticEvent> events,
        CancellationToken cancellationToken)
    {
        await foreach (var diagnosticEvent in events.ReadAllAsync(cancellationToken))
            try
            {
                using var activity = StartOperationActivity("deployment.diagnostic.deliver", diagnosticEvent);
                var startedAt = Stopwatch.GetTimestamp();
                await DeliverToTerminalAsync(diagnosticEvent);
                AutoMateTelemetry.EventsDelivered.Add(1, TelemetryTags.Create(diagnosticEvent));
                var tags = TelemetryTags.Create(diagnosticEvent);
                tags.Add("deployment.sink", "terminal");
                AutoMateTelemetry.SinkDuration.Record(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, tags);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AutoMateTelemetry.DeliveryFailures.Add(1, TelemetryTags.Create(diagnosticEvent));
                logger.LogWarning(exception,
                    "Failed to deliver deployment diagnostic. Source {Source} kind {Kind} project {ProjectId}.",
                    diagnosticEvent.Source, diagnosticEvent.Kind, diagnosticEvent.ProjectId);
            }
    }

    private Task DeliverToTerminalAsync(DeploymentDiagnosticEvent diagnosticEvent)
    {
        return diagnosticEvent.TerminalChannel.Kind switch
        {
            DeploymentTerminalChannelKind.Build or DeploymentTerminalChannelKind.System or
                DeploymentTerminalChannelKind.Container => logStreamer.StreamTerminalLogAsync(diagnosticEvent.ProjectId,
                    GetTerminalChannel(diagnosticEvent), diagnosticEvent.Message),
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

    private void TryWrite(ChannelWriter<DeploymentDiagnosticEvent> writer, DeploymentDiagnosticEvent diagnosticEvent,
        string sinkName)
    {
        if (writer.TryWrite(diagnosticEvent)) return;
        AutoMateTelemetry.EventsDropped.Add(1, TelemetryTags.Create(diagnosticEvent));
        logger.LogWarning(
            "Dropped deployment diagnostic because the {SinkName} queue is full. Source {Source} kind {Kind} project {ProjectId}.",
            sinkName, diagnosticEvent.Source, diagnosticEvent.Kind, diagnosticEvent.ProjectId);
    }

    private static Channel<DeploymentDiagnosticEvent> CreateSinkChannel(int capacity)
    {
        return Channel.CreateBounded<DeploymentDiagnosticEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    }

    private static Activity? StartOperationActivity(string operationName, DeploymentDiagnosticEvent diagnosticEvent)
    {
        if (!string.IsNullOrWhiteSpace(diagnosticEvent.TraceId) &&
            !string.IsNullOrWhiteSpace(diagnosticEvent.SpanId) &&
            ActivityContext.TryParse($"00-{diagnosticEvent.TraceId}-{diagnosticEvent.SpanId}-01", null, false,
                out var parentContext))
            return AutoMateTelemetry.Deployments.StartActivity(operationName, ActivityKind.Internal,
                parentContext);

        return AutoMateTelemetry.Deployments.StartActivity(operationName);
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