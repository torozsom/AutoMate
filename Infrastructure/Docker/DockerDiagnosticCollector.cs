using System.Buffers;
using System.Globalization;
using System.Net;
using System.Threading.Channels;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.Docker;
using Application.Diagnostics;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Docker;

/// <summary>Owns cancellable Docker subscriptions, bounded parsing and durable overlap confirmation.</summary>
internal sealed class DockerDiagnosticCollector(
    IDockerClient client,
    IDeploymentDiagnosticPublisher diagnostics,
    IDiagnosticRedactor redactor,
    TimeProvider clock,
    ILogger logger,
    IServiceScopeFactory? scopes = null)
{
    /// <summary>Verifies metrics ownership before starting a CLI stream bound to an immutable container ID.</summary>
    internal async Task<string?> VerifyContainerAsync(DockerDeploymentTarget target, DockerContainerTarget container,
        CancellationToken token)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var inspect = await client.Containers.InspectContainerAsync(container.Name, deadline.Token);
            if (DockerDiagnosticNormalizer.IsOwned(target, container, inspect.Config?.Labels, true)) return inspect.ID;
        }
        catch (DockerContainerNotFoundException) when (!token.IsCancellationRequested)
        {
            // Compose may still be building, or may just have removed the container during stop.
            return null;
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            Failed(DeploymentDiagnosticSource.DockerContainer, exception);
        }

        await NoticeAsync(target, container, DeploymentDiagnosticSource.DockerContainer,
            "Container metrics are unavailable or container ownership could not be verified; collection will retry.",
            token);
        return null;
    }

    /// <summary>Subscribes before Compose runs and reconnects with a bounded, label-filtered event overlap.</summary>
    internal async Task MonitorDaemonAsync(DockerDeploymentTarget target, Action subscribed, CancellationToken token)
    {
        var lastSecond = target.RegisteredAt.ToUnixTimeSeconds() - 1;
        var delivered = new HashSet<Guid>();
        var order = new Queue<Guid>();
        var attempt = 0;
        var ready = false;
        var gapReported = false;
        while (!token.IsCancellationRequested)
        {
            using var activity =
                DeploymentTracing.Start(DeploymentOperation.DockerEvents, target.ProjectId, target.DeploymentId);
            try
            {
                using var subscription = CancellationTokenSource.CreateLinkedTokenSource(token);
                var pending = Channel.CreateBounded<DeploymentDiagnosticEvent>(new BoundedChannelOptions(256)
                    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
                var omitted = 0;
                var receiver = ReceiveAsync();
                if (!ready)
                {
                    subscribed();
                    ready = true;
                }

                try
                {
                    await foreach (var e in pending.Reader.ReadAllAsync(token))
                    {
                        if (Interlocked.Exchange(ref omitted, 0) > 0)
                            await NoticeAsync(target, null, DeploymentDiagnosticSource.DockerDaemon,
                                "Docker lifecycle admission exceeded its bounded window; some events may be missing.",
                                token);
                        await EmitAsync(e);
                    }

                    await receiver;
                    if (Interlocked.Exchange(ref omitted, 0) > 0)
                        await NoticeAsync(target, null, DeploymentDiagnosticSource.DockerDaemon,
                            "Docker lifecycle admission exceeded its bounded window; some events may be missing.",
                            token);
                }
                finally
                {
                    await subscription.CancelAsync();
                    try
                    {
                        await receiver;
                    }
                    catch (OperationCanceledException) when (subscription.IsCancellationRequested)
                    {
                    }
                    catch (Exception exception)
                    {
                        Failed(DeploymentDiagnosticSource.DockerDaemon, exception);
                    }
                }

                // The SDK owns JSON framing. Only normalized, redacted events enter the bounded queue.
                /// <summary>Normalizes synchronous SDK observations and awaits the cancellable receiver.</summary>
                async Task ReceiveAsync()
                {
                    try
                    {
                        await client.System.MonitorEventsAsync(new ContainerEventsParameters
                        {
                            Since = lastSecond.ToString(CultureInfo.InvariantCulture),
                            Filters = new Dictionary<string, IDictionary<string, bool>>
                            {
                                ["type"] = new Dictionary<string, bool> { ["container"] = true },
                                ["label"] = new Dictionary<string, bool>
                                {
                                    ["io.automate.owner=AutoMate"] = true,
                                    [$"io.automate.project={target.ProjectId:N}"] = true
                                }
                            }
                        }, new DaemonProgress(message =>
                        {
                            try
                            {
                                if (DockerDiagnosticNormalizer.Daemon(target, message) is { } e &&
                                    !pending.Writer.TryWrite(redactor.Redact(e).Event))
                                    Interlocked.Increment(ref omitted);
                            }
                            catch (Exception exception)
                            {
                                Failed(DeploymentDiagnosticSource.DockerDaemon, exception);
                            }
                        }), subscription.Token);
                        pending.Writer.TryComplete();
                    }
                    catch (Exception exception)
                    {
                        pending.Writer.TryComplete(exception);
                        throw;
                    }
                }

                DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Completed);
                await NoticeAsync(target, null, DeploymentDiagnosticSource.DockerDaemon,
                    "Docker lifecycle subscription ended; reconnecting with a bounded overlap.", token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Canceled);
                return;
            }
            catch (Exception exception)
            {
                DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Failed);
                Failed(DeploymentDiagnosticSource.DockerDaemon, exception);
                if (!ready)
                {
                    subscribed();
                    ready = true;
                }

                if (attempt == 0)
                    await NoticeAsync(target, null, DeploymentDiagnosticSource.DockerDaemon,
                        "Docker lifecycle subscription is unavailable. Check daemon connectivity and event permissions; retrying. Some lifecycle events may be missing.",
                        token);
            }

            await Task.Delay(Backoff(++attempt), clock, token);

            /// <summary>Confirms owned lifecycle ingestion before advancing the overlap checkpoint.</summary>
            async Task EmitAsync(DeploymentDiagnosticEvent e)
            {
                if (delivered.Contains(e.EventId!.Value))
                {
                    Duplicate(e.Source);
                    return;
                }

                if (!await ConfirmAsync(e, token)) throw new IOException("Diagnostic admission was not confirmed.");
                if (order.Count >= 4096)
                {
                    delivered.Remove(order.Dequeue());
                    if (!gapReported)
                    {
                        gapReported = true;
                        await NoticeAsync(target, null, DeploymentDiagnosticSource.DockerDaemon,
                            "Docker lifecycle overlap exceeded its checkpoint window; repeated events may appear after reconnect.",
                            token);
                    }
                }

                if (attempt > 0) Recovered(DeploymentDiagnosticSource.DockerDaemon);
                attempt = 0;
                delivered.Add(e.EventId.Value);
                order.Enqueue(e.EventId.Value);
                lastSecond = Math.Max(lastSecond, e.TimestampUtc.ToUnixTimeSeconds() - 1);
            }
        }
    }

    /// <summary>Verifies container ownership, decodes separate streams and retries using confirmed timestamp overlap.</summary>
    internal async Task MonitorContainerAsync(DockerDeploymentTarget target, DockerContainerTarget container,
        CancellationToken token)
    {
        var window = new DockerLogReplayWindow();
        string? previousId = null;
        var attempt = 0;
        var ended = false;
        var ttyReported = false;
        var gapReported = false;
        while (!token.IsCancellationRequested)
        {
            using var activity =
                DeploymentTracing.Start(DeploymentOperation.DockerLogs, target.ProjectId, target.DeploymentId);
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
                connect.CancelAfter(TimeSpan.FromSeconds(10));
                var inspect = await client.Containers.InspectContainerAsync(container.Name, connect.Token);
                if (!DockerDiagnosticNormalizer.IsOwned(target, container, inspect.Config?.Labels, true))
                {
                    await NoticeAsync(target, container, DeploymentDiagnosticSource.DockerContainer,
                        "Container output was not collected because its project/deployment ownership could not be verified.",
                        token);
                    return;
                }

                if (previousId != inspect.ID)
                {
                    window = new DockerLogReplayWindow();
                    await RestoreAsync(target, inspect.ID, window, token);
                    previousId = inspect.ID;
                    ended = false;
                }

                if (ended && inspect.State?.Running != true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), clock, token);
                    continue;
                }

                window.BeginSubscription();
                var tty = inspect.Config?.Tty == true;
                if (tty && !ttyReported)
                {
                    ttyReported = true;
                    await NoticeAsync(target, container, DeploymentDiagnosticSource.DockerContainer,
                        "This container uses a TTY; Docker combines stdout and stderr into one output stream.", token);
                }

                using var stream = await client.Containers.GetContainerLogsAsync(inspect.ID,
                    inspect.Config?.Tty == true,
                    new ContainerLogsParameters
                    {
                        ShowStdout = true,
                        ShowStderr = true,
                        Follow = true,
                        Timestamps = true,
                        Tail = window.LastTimestamp is null ? "100" : "all",
                        Since = window.LastTimestamp is { } last
                            ? (last.ToUnixTimeSeconds() - 1).ToString(CultureInfo.InvariantCulture)
                            : null
                    }, connect.Token);
                connect.CancelAfter(Timeout.InfiniteTimeSpan);
                if (attempt > 0 || ended)
                {
                    Recovered(DeploymentDiagnosticSource.DockerContainer);
                    await NoticeAsync(target, container, DeploymentDiagnosticSource.DockerContainer,
                        "Container output subscription recovered using saved timestamp overlap.", token);
                }

                attempt = 0;
                ended = false;
                var stdout = new DockerLogDecoder(true, clock);
                var stderr = new DockerLogDecoder(true, clock);
                var bytes = ArrayPool<byte>.Shared.Rent(8192);
                try
                {
                    while (true)
                    {
                        var read = await stream.ReadOutputAsync(bytes, 0, 8192, token);
                        if (read.EOF) break;
                        var error = read.Target == MultiplexedStream.TargetStream.StandardError;
                        foreach (var line in (error ? stderr : stdout).Feed(bytes.AsSpan(0, read.Count)))
                            await EmitAsync(line,
                                tty ? DeploymentDiagnosticStream.Control :
                                error ? DeploymentDiagnosticStream.StandardError :
                                DeploymentDiagnosticStream.StandardOutput);
                    }

                    foreach (var line in stdout.Feed([], true))
                        await EmitAsync(line,
                            tty ? DeploymentDiagnosticStream.Control : DeploymentDiagnosticStream.StandardOutput);
                    foreach (var line in stderr.Feed([], true))
                        await EmitAsync(line, DeploymentDiagnosticStream.StandardError);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(bytes);
                }

                ended = true;
                DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Completed);
                await NoticeAsync(target, container, DeploymentDiagnosticSource.DockerContainer,
                    "Container output stream ended; waiting for the container to run again.", token,
                    DeploymentDiagnosticKind.Lifecycle);

                /// <summary>Redacts before deriving replay identity and confirms each source occurrence durably.</summary>
                async Task EmitAsync(DockerLogLine line, DeploymentDiagnosticStream output)
                {
                    var e = redactor.Redact(new DeploymentDiagnosticEvent(target.ProjectId, target.DeploymentId,
                        DeploymentDiagnosticSource.DockerContainer,
                        line.Omitted ? DeploymentDiagnosticKind.Annotation : DeploymentDiagnosticKind.Log,
                        line.Omitted ? DeploymentDiagnosticSeverity.Warning : DeploymentDiagnosticSeverity.Information,
                        line.Timestamp, line.Text,
                        new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, container.Channel),
                        new Dictionary<string, string> { ["phase"] = "runtime", ["stream"] = output.ToString() },
                        SourceIdentity: new DeploymentDiagnosticSourceIdentity(container.IsDatabase
                            ? DeploymentDiagnosticComponent.Database
                            : DeploymentDiagnosticComponent.Web, output, inspect.ID))).Event;
                    var occurrence = line.ProviderTimestamp is null
                        ? null
                        : window.Observe(line.ProviderTimestamp, output, e.Message);
                    if (line.ProviderTimestamp is not null && occurrence is null)
                    {
                        Duplicate(e.Source);
                        return;
                    }

                    if (occurrence is { } cursor)
                        e = e with
                        {
                            Cursor = cursor.Cursor,
                            EventId = DockerDiagnosticNormalizer.Identity(
                                $"{target.DeploymentId:N}/{inspect.ID}/{cursor.Key}/{cursor.Occurrence}")
                        };
                    if (!await ConfirmAsync(e, token)) throw new IOException("Diagnostic admission was not confirmed.");
                    if (occurrence is { } confirmation)
                        window.Confirm(confirmation.Key, confirmation.Occurrence, e.TimestampUtc);
                    if (window.Evicted && !gapReported)
                    {
                        gapReported = true;
                        await NoticeAsync(target, container, DeploymentDiagnosticSource.DockerContainer,
                            "Container replay exceeded its checkpoint window; repeated output may appear after reconnect.",
                            token);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Canceled);
                return;
            }
            catch (DockerContainerNotFoundException) when (previousId is null && !token.IsCancellationRequested)
            {
                DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Completed);
                if (attempt == 0)
                    await NoticeAsync(target, container, DeploymentDiagnosticSource.DockerContainer,
                        "Waiting for the deployment container to be created; output collection will retry.", token,
                        DeploymentDiagnosticKind.Lifecycle);
            }
            catch (Exception exception)
            {
                DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Failed);
                Failed(DeploymentDiagnosticSource.DockerContainer, exception);
                if (attempt == 0)
                    await NoticeAsync(target, container, DeploymentDiagnosticSource.DockerContainer,
                        exception is DockerApiException
                        {
                            StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                        }
                            ? "Container output subscription was denied. Check Docker permissions; retrying."
                            : "Container output is temporarily unavailable. Check Docker connectivity/logging configuration; retrying. Partial output may be replayed.",
                        token);
            }

            await Task.Delay(Backoff(++attempt), clock, token);
        }
    }

    /// <summary>Restores only bounded redacted history; failures never select a database log payload fallback.</summary>
    private async Task RestoreAsync(DockerDeploymentTarget target, string containerId, DockerLogReplayWindow window,
        CancellationToken token)
    {
        if (scopes is null) return;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var history = await scope.ServiceProvider.GetRequiredService<IDeploymentDiagnosticStore>()
                .ReadRecentAsync(target.ProjectId, target.DeploymentId, 500, token);
            window.Restore(history.Events, containerId);
            if (!history.CanAdvanceCursor || history.EarlierOmitted || history.Availability is not null)
                await NoticeAsync(target, null, DeploymentDiagnosticSource.DockerDaemon,
                    "Local output recovery has a bounded or unavailable saved history; some recent output may repeat.",
                    token);
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            logger.LogWarning("Docker replay checkpoint unavailable: {FailureType}.", exception.GetType().Name);
            await NoticeAsync(target, null, DeploymentDiagnosticSource.DockerDaemon,
                "Saved local output checkpoints are unavailable; recent output may repeat.", token);
        }
    }

    /// <summary>Requires durable ingestion before updating source cursors.</summary>
    private Task<bool> ConfirmAsync(DeploymentDiagnosticEvent e, CancellationToken token)
    {
        return diagnostics is IDurableDeploymentDiagnosticPublisher durable
            ? durable.PublishDurablyAsync(e, token)
            : Task.FromResult(false);
    }

    /// <summary>Emits a safe, source-aware annotation or end-of-stream event through central redaction.</summary>
    private ValueTask NoticeAsync(DockerDeploymentTarget target, DockerContainerTarget? container,
        DeploymentDiagnosticSource source,
        string message, CancellationToken token, DeploymentDiagnosticKind kind = DeploymentDiagnosticKind.Annotation)
    {
        return diagnostics.PublishAsync(new DeploymentDiagnosticEvent(target.ProjectId, target.DeploymentId, source,
            kind,
            kind == DeploymentDiagnosticKind.Lifecycle
                ? DeploymentDiagnosticSeverity.Information
                : DeploymentDiagnosticSeverity.Warning,
            clock.GetUtcNow(), $"\r\n[Docker{(container is null ? "" : " " + container.Channel)}] {message}\r\n",
            container is null
                ? new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Build)
                : new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, container.Channel),
            new Dictionary<string, string> { ["phase"] = "subscription" }), token);
    }

    /// <summary>Reports only source and failure type, never raw Docker error bodies.</summary>
    private void Failed(DeploymentDiagnosticSource source, Exception exception)
    {
        AutoMateTelemetry.CollectorErrors.Add(1,
            new KeyValuePair<string, object?>("deployment.source", source.ToString()));
        logger.LogWarning("Docker collector unavailable. Source {Source} failure {FailureType}.", source,
            exception.GetType().Name);
    }

    /// <summary>Counts successful reconnects without container/project identifiers as labels.</summary>
    private static void Recovered(DeploymentDiagnosticSource source)
    {
        AutoMateTelemetry.CollectorReconnects.Add(1,
            new KeyValuePair<string, object?>("deployment.source", source.ToString()));
    }

    /// <summary>Counts checkpoint suppression without recording message content.</summary>
    private static void Duplicate(DeploymentDiagnosticSource source)
    {
        AutoMateTelemetry.DiagnosticDuplicates.Add(1,
            new KeyValuePair<string, object?>("deployment.source", source.ToString()));
    }

    /// <summary>Caps reconnect delays at thirty seconds.</summary>
    private static TimeSpan Backoff(int attempt)
    {
        return TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(5, attempt - 1))));
    }

    /// <summary>Runs Docker event callbacks synchronously so parsing and admission remain ordered.</summary>
    private sealed class DaemonProgress(Action<Message> report) : IProgress<Message>
    {
        /// <inheritdoc />
        public void Report(Message value)
        {
            report(value);
        }
    }
}