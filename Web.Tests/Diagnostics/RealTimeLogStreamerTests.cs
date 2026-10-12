using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Application.Abstractions.Diagnostics;
using Application.Diagnostics;
using Infrastructure.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Web.Hubs;
using Web.Services;
using Xunit;

namespace Web.Tests;

/// <summary>
///     Verifies that deadlines cancel real transport writes while retaining browser callback
///     compatibility.
/// </summary>
public sealed class RealTimeLogStreamerTests
{
    /// <summary>
    ///     Typed delivery preserves native timestamp/identity and accepts only the fixed numeric
    ///     catalog.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Typed_metrics_preserve_identity_time_and_filter_invalid_samples()
    {
        var calls = new List<(string Method, object?[] Arguments)>();
        var project = Guid.NewGuid();
        var streamer = Create(project, (method, args, _) =>
        {
            calls.Add((method, args));
            return Task.CompletedTask;
        });
        var snapshot = new DeploymentRuntimeMetricSnapshot(project, Guid.NewGuid(), "cosmos",
            DeploymentDiagnosticComponent.Database,
            DateTimeOffset.Parse("2026-10-10T11:00:00Z"),
            [
                new DeploymentMetricSample("automate_database_ru_usage_percent", 42, "percent"),
                new DeploymentMetricSample("arbitrary", 1, "percent"),
                new DeploymentMetricSample("automate_cpu_usage_cores", double.NaN, "cores"),
                new DeploymentMetricSample("automate_memory_used_bytes", 1, "percent")
            ]);
        await streamer.StreamDeploymentMetricsAsync(snapshot);
        var call = Assert.Single(calls);
        Assert.Equal(nameof(ILogClient.ReceiveDeploymentMetrics), call.Method);
        var delivered = Assert.IsType<DeploymentRuntimeMetricSnapshot>(call.Arguments[0]);
        Assert.Equal(snapshot.ProjectId, delivered.ProjectId);
        Assert.Equal(snapshot.DeploymentId, delivered.DeploymentId);
        Assert.Equal(snapshot.Timestamp, delivered.Timestamp);
        Assert.Equal(snapshot.Component, delivered.Component);
        Assert.Equal(snapshot.Samples[0], Assert.Single(delivered.Samples));
    }

    /// <summary>
    ///     Terminal/notice/metric callbacks apply the final policy even to legacy or bypassed caller
    ///     text.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Every_transport_callback_masks_sensitive_text_without_changing_cursors_or_groups()
    {
        var calls = new List<(string Method, object?[] Arguments)>();
        var project = Guid.NewGuid();
        var streamer = Create(project, (method, args, _) =>
        {
            calls.Add((method, args));
            return Task.CompletedTask;
        });
        var deployment = Guid.NewGuid();
        var log = new DeploymentTerminalLog(17, project, deployment, "web", "{\"password\":\"private-text\"}",
            SourceInstanceId: "Bearer private-instance", SourceCursor: "token=private-cursor");
        await streamer.StreamTerminalLogAsync(log);
        await streamer.StreamTerminalNoticeAsync(project, "Cookie: private-notice");
        await streamer.StreamContainerMetricsAsync(project, "api_key=private-name", "password=private-cpu",
            "token=private-memory");
        Assert.Equal(3, calls.Count);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(calls.Select(call => call.Arguments)));
        var delivered = Assert.IsType<DeploymentTerminalLog>(calls[0].Arguments[0]);
        Assert.Equal(17, delivered.OrderId);
        Assert.Equal(project, delivered.ProjectId);
        Assert.Equal(deployment, delivered.DeploymentId);
        Assert.Equal("web", delivered.TerminalChannel);
        Assert.Contains("private-text", log.Message);
    }

    /// <summary>
    ///     The transport receives the same callback names, arguments and authorized project group as
    ///     before.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Sends_preserve_callback_contract_and_project_routing()
    {
        var calls = new List<(string Method, object?[] Arguments)>();
        var project = Guid.NewGuid();
        var streamer = Create(project, (method, args, _) =>
        {
            calls.Add((method, args));
            return Task.CompletedTask;
        });
        var log = new DeploymentTerminalLog(1, project, Guid.NewGuid(), "build", "safe output");
        await streamer.StreamTerminalLogAsync(log);
        await streamer.StreamTerminalNoticeAsync(project, "notice");
        await streamer.StreamContainerMetricsAsync(project, "web", "1%", "2MiB");
        Assert.Equal([
            nameof(ILogClient.ReceiveTerminalLog), nameof(ILogClient.ReceiveTerminalNotice),
            nameof(ILogClient.ReceiveContainerMetrics)
        ], calls.Select(c => c.Method));
        Assert.Equal(log, calls[0].Arguments[0]);
        Assert.Equal(["web", "1%", "2MiB"], calls[2].Arguments);
    }

    /// <summary>
    ///     Expiration cancels the underlying pending send; a later send remains usable.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Slow_write_is_canceled_and_later_output_is_delivered()
    {
        var project = Guid.NewGuid();
        var pending = 0;
        var writes = 0;
        var streamer = Create(project, async (_, _, token) =>
        {
            if (++writes != 1) return;
            Interlocked.Increment(ref pending);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                Interlocked.Decrement(ref pending);
            }
        });
        await Assert.ThrowsAsync<TimeoutException>(() => streamer.StreamTerminalNoticeAsync(project, "slow"));
        Assert.Equal(0, pending);
        await streamer.StreamTerminalNoticeAsync(project, "later");
        Assert.Equal(2, writes);
    }

    /// <summary>
    ///     Caller shutdown is propagated as cancellation rather than a transport timeout.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Caller_cancellation_reaches_the_transport()
    {
        var project = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamer = Create(project, async (_, _, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        using var canceled = new CancellationTokenSource();
        var send = streamer.StreamTerminalNoticeAsync(project, "cancel", canceled.Token);
        await entered.Task;
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
    }

    /// <summary>
    ///     Real send spans distinguish deadline expiry from caller cancellation without exporting
    ///     payloads.
    /// </summary>
    /// <param name="cancel">Whether to cancel the controlled operation.</param>
    /// <param name="outcome">Configured or expected operation outcome.</param>
    /// <param name="status">Expected or synthetic trace status.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData(false, "timed_out", ActivityStatusCode.Error)]
    [InlineData(true, "canceled", ActivityStatusCode.Unset)]
    public async Task Send_spans_distinguish_caller_cancellation_from_deadline(bool cancel, string outcome,
        ActivityStatusCode status)
    {
        using var exporter = new TraceSnapshotExporter();
        using var provider = Sdk.CreateTracerProviderBuilder().AddSource(AutoMateTelemetry.Deployments.Name)
            .AddProcessor(new SimpleActivityExportProcessor(exporter)).Build();
        var project = Guid.NewGuid();
        using var canceled = new CancellationTokenSource();
        var streamer = Create(project, async (_, _, token) =>
        {
            if (cancel) canceled.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                streamer.StreamTerminalNoticeAsync(project, "private-message", canceled.Token));
        else
            await Assert.ThrowsAsync<TimeoutException>(() =>
                streamer.StreamTerminalNoticeAsync(project, "private-message", canceled.Token));
        var span = Assert.Single(exporter.Spans,
            span => Equals(span.Tags.GetValueOrDefault("deployment.project.id"), project));
        Assert.Equal("deployment.signalr.send", span.Name);
        Assert.Equal(outcome, span.Tags["deployment.outcome"]);
        Assert.Equal(status, span.Status);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(span));
    }

    /// <summary>
    ///     Creates a SignalR context exposing the actual cancellable client-proxy send contract.
    /// </summary>
    /// <param name="project">Synthetic project identity.</param>
    /// <param name="send">Configured SignalR send callback receiving method, arguments and cancellation.</param>
    /// <returns>The configured real time log streamer instance or response for this scenario.</returns>
    private static RealTimeLogStreamer Create(Guid project,
        Func<string, object?[], CancellationToken, Task> send)
    {
        var proxy = Stub<IClientProxy>((_, args) => send((string)args![0]!, (object?[])args[1]!,
            (CancellationToken)args[2]!));
        var clients = Stub<IHubClients>((method, args) =>
        {
            Assert.Equal("Group", method!.Name);
            Assert.Equal($"project-{project}", args![0]);
            return proxy;
        });
        var context = Stub<IHubContext<LogHub>>((_, _) => clients);
        return new RealTimeLogStreamer(context,
            Options.Create(new DeploymentDiagnosticOptions { DeliveryTimeoutSeconds = 1 }),
            new DiagnosticRedactor());
    }

    /// <summary>
    ///     Creates interface collaborators without extra dependencies.
    /// </summary>
    /// <param name="handler">Configured HTTP handler or reflected port callback.</param>
    /// <typeparam name="T">Interface or result type created by the fixture.</typeparam>
    /// <returns>The configured t instance or response for this scenario.</returns>
    private static T Stub<T>(Func<MethodInfo?, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, InterfaceStub>();
        ((InterfaceStub)(object)proxy).Handler = handler;
        return proxy;
    }

    /// <summary>
    ///     Routes calls to the scenario-specific behavior.
    /// </summary>
    public class InterfaceStub : DispatchProxy
    {
        /// <summary>Behavior for the intercepted method.</summary>
        public Func<MethodInfo?, object?[]?, object?> Handler { get; set; } = null!;

        /// <summary>
        ///     Dispatches supported synthetic port calls to configured responses or the fixture callback.
        /// </summary>
        /// <param name="targetMethod">Reflected port member dispatched by the fake.</param>
        /// <param name="args">Arguments for the selected fixture port or reflected component member.</param>
        /// <returns>
        ///     The configured response for the selected fixture member, including null when its contract
        ///     requires no value.
        /// </returns>
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return Handler(targetMethod, args);
        }
    }
}