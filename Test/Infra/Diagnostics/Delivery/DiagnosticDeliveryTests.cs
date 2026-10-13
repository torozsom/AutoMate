using System.Collections.Concurrent;
using System.Diagnostics;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.Logging;
using Application.Diagnostics;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Test.Infra.Diagnostics;

/// <summary>
///     Verifies sink deadlines, durable confirmations and later-event recovery without database
///     payload writes.
/// </summary>
/// <exception cref="TimeoutException">The bounded fixture wait expires before the expected signal.</exception>
public sealed class DiagnosticDeliveryTests
{
    /// <summary>
    ///     A stalled write or provider cancellation is isolated from later redacted output.
    /// </summary>
    /// <param name="failure">Failure flag or failure mode injected by the fixture.</param>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Theory]
    [InlineData("storage-timeout")]
    [InlineData("delivery-timeout")]
    [InlineData("storage-cancellation")]
    public async Task Dispatcher_recovers_after_a_sink_failure(string failure)
    {
        var store = new TestStore { Failure = failure };
        var live = new TestLive { StallNext = failure == "delivery-timeout" };
        await using var services = Services(store, live);
        var publisher = Publisher(services);
        using var worker = new DeploymentDiagnosticDispatcher(publisher, live,
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<DeploymentDiagnosticDispatcher>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await publisher.PublishAsync(Event() with { Message = "first" });
            await publisher.PublishAsync(Event() with { Message = "password=private-value" });
            await live.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(live.Delivered, log => log.Message == "password=[REDACTED]");
            Assert.DoesNotContain(store.Saved, e => e.Message.Contains("private-value", StringComparison.Ordinal));
            if (failure == "delivery-timeout") Assert.Equal(2, store.Saved.Count);
            else Assert.Single(store.Saved);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    ///     Only durable storage confirmation advances a collector cursor, regardless of live delivery
    ///     success.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Durable_ingestion_retains_confirmation_when_live_delivery_times_out()
    {
        var store = new TestStore();
        var live = new TestLive { StallNext = true };
        await using var services = Services(store, live);
        var publisher = Publisher(services);
        Assert.True(await publisher.PublishDurablyAsync(Event() with { Message = "password=private-value" },
            CancellationToken.None));
        var saved = Assert.Single(store.Saved);
        Assert.Equal("password=[REDACTED]", saved.Message);
        Assert.NotNull(saved.TraceId);
        Assert.NotNull(saved.SpanId);
        Assert.NotNull(saved.EventId);
        store.Failure = "storage-timeout";
        Assert.False(await publisher.PublishDurablyAsync(Event() with { Message = "retry later" },
            CancellationToken.None));
        Assert.Single(store.Saved);
    }

    /// <summary>
    ///     Host shutdown cancels the pending write instead of reporting it as a recoverable storage
    ///     outage.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Durable_ingestion_propagates_caller_cancellation()
    {
        var store = new TestStore { Failure = "storage-timeout" };
        await using var services = Services(store, new TestLive());
        using var canceled = new CancellationTokenSource();
        var pending = Publisher(services).PublishDurablyAsync(Event() with { Message = "cancel" }, canceled.Token);
        await store.Entered.Task;
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(store.Saved);
    }

    /// <summary>
    ///     The asynchronous dispatcher restores the collector's trace instead of creating an unrelated
    ///     root.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Dispatch_activity_retains_the_ingestion_trace()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ActivityStopped = spans.Enqueue,
            ShouldListenTo = source => source.Name == "AutoMate.Deployments",
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        var store = new TestStore();
        var live = new TestLive();
        await using var services = Services(store, live);
        var publisher = Publisher(services);
        using var worker = new DeploymentDiagnosticDispatcher(publisher, live,
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<DeploymentDiagnosticDispatcher>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await publisher.PublishAsync(Event() with { Message = "password=private-value" });
            await live.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var saved = Assert.Single(store.Saved);
            Assert.Equal(saved.TraceId, store.DispatchTrace);
            var owned = spans.Where(span => Equals(span.GetTagItem("deployment.id"), saved.DeploymentId)).ToArray();
            foreach (var name in new[] { "deployment.diagnostic.redact", "deployment.diagnostic.persist" })
                Assert.Contains(owned, span => span.OperationName == name && span.TraceId.ToString() == saved.TraceId &&
                                               span.Status == ActivityStatusCode.Ok);
            Assert.All(owned,
                span => Assert.DoesNotContain("private-value",
                    string.Join(" ", span.TagObjects.Select(tag => tag.Value))));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    ///     Overflow annotations must not reuse the persisted event identity or metric payload they
    ///     follow.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Overflow_marker_has_an_independent_durable_identity()
    {
        var store = new TestStore();
        var live = new TestLive();
        await using var services = Services(store, live);
        var publisher = Publisher(services);
        var original = Event() with { Message = "build", EventId = Guid.NewGuid() };
        for (var index = 0; index < 513; index++) await publisher.PublishAsync(original);
        using var worker = new DeploymentDiagnosticDispatcher(publisher, live,
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<DeploymentDiagnosticDispatcher>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var marker = await store.Marker.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(original.EventId, marker.EventId);
            Assert.NotNull(marker.EventId);
            Assert.Null(marker.Metrics);
            Assert.Contains("1 diagnostic event(s) omitted", marker.Message);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    ///     Creates scoped store access without registering EF or a PostgreSQL diagnostic sink.
    /// </summary>
    /// <param name="store">Controlled persistence sink.</param>
    /// <param name="live">Controlled live-delivery sink.</param>
    /// <returns>The configured fixture dependency provider, owned by the caller.</returns>
    private static ServiceProvider Services(TestStore store, TestLive live)
    {
        return new ServiceCollection().AddSingleton<IDeploymentDiagnosticStore>(store)
            .AddSingleton<ILogStreamer>(live)
            .AddSingleton<IOptions<TelemetryStorageOptions>>(Options.Create(new TelemetryStorageOptions
                { Backend = "LokiMimir", DeliveryMode = "DiskGateway" })).BuildServiceProvider();
    }

    /// <summary>
    ///     Uses short production-supported deadlines for failure scenarios.
    /// </summary>
    /// <param name="services">Fixture dependency provider.</param>
    /// <returns>The configured deployment diagnostic publisher instance or response for this scenario.</returns>
    private static DeploymentDiagnosticPublisher Publisher(ServiceProvider services)
    {
        return new DeploymentDiagnosticPublisher(new DiagnosticRedactor(),
            Options.Create(
                new DeploymentDiagnosticOptions { PersistenceTimeoutSeconds = 1, DeliveryTimeoutSeconds = 1 }),
            NullLogger<DeploymentDiagnosticPublisher>.Instance, services.GetRequiredService<IServiceScopeFactory>());
    }

    /// <summary>
    ///     Builds non-sensitive routing metadata; each test assigns its untrusted message separately.
    /// </summary>
    /// <returns>The configured deployment diagnostic event instance or response for this scenario.</returns>
    private static DeploymentDiagnosticEvent Event()
    {
        return new DeploymentDiagnosticEvent(Guid.NewGuid(), Guid.NewGuid(), DeploymentDiagnosticSource.DockerCompose,
            DeploymentDiagnosticKind.Log, DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow, "fixture",
            new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Build));
    }

    /// <summary>
    ///     Simulates storage failure once and retains only confirmed, safe events.
    /// </summary>
    private sealed class TestStore : IDeploymentDiagnosticStore
    {
        /// <summary>Next storage failure scenario.</summary>
        public string? Failure { get; set; }

        /// <summary>Durably confirmed events.</summary>
        public ConcurrentQueue<DeploymentDiagnosticEvent> Saved { get; } = new();

        /// <summary>Signals that admission has started.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Trace observed at persistence inside the asynchronous dispatcher.</summary>
        public string? DispatchTrace { get; private set; }

        /// <summary>Signals confirmation of an overflow annotation.</summary>
        public TaskCompletionSource<DeploymentDiagnosticEvent> Marker { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        ///     Records persistence and trace observations while optionally injecting timeout or
        ///     cancellation.
        /// </summary>
        /// <param name="e">Browser change or keyboard event.</param>
        /// <param name="channel">Recorded log channel to select; ignored by this fixture.</param>
        /// <param name="token">Cancellation token for the fixture operation.</param>
        /// <returns>A task returning the fixture count or ordering position.</returns>
        /// <exception cref="OperationCanceledException">Cancellation is requested or deliberately injected by the fixture.</exception>
        public async Task<long> PersistAsync(DeploymentDiagnosticEvent e, string? channel,
            CancellationToken token = default)
        {
            Entered.TrySetResult();
            var failure = Failure;
            Failure = null;
            if (failure == "storage-timeout") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            if (failure == "storage-cancellation") throw new OperationCanceledException("provider canceled");
            DispatchTrace = Activity.Current?.TraceId.ToString();
            Saved.Enqueue(e);
            if (e.Kind == DeploymentDiagnosticKind.Annotation) Marker.TrySetResult(e);
            return Saved.Count;
        }

        /// <summary>
        ///     Rejects read recent because this member is deliberately unavailable in the fixture.
        /// </summary>
        /// <param name="projectId">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="deploymentId">Synthetic deployment identity; ignored by this fixture.</param>
        /// <param name="limit">Requested count or configured admission bound; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>This deliberately unavailable member never returns successfully.</returns>
        /// <exception cref="NotImplementedException">This port member is not implemented by the fixture.</exception>
        public Task<DeploymentTerminalHistory> ReadRecentAsync(Guid projectId, Guid deploymentId, int limit,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        ///     Rejects read after because this member is deliberately unavailable in the fixture.
        /// </summary>
        /// <param name="projectId">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="deploymentId">Synthetic deployment identity; ignored by this fixture.</param>
        /// <param name="cursor">Opaque history position, or null to read the latest page; ignored by this fixture.</param>
        /// <param name="limit">Requested count or configured admission bound; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>This deliberately unavailable member never returns successfully.</returns>
        /// <exception cref="NotImplementedException">This port member is not implemented by the fixture.</exception>
        public Task<DeploymentTerminalHistory> ReadAfterAsync(Guid projectId, Guid deploymentId, long cursor, int limit,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        ///     Rejects delete expired because this member is deliberately unavailable in the fixture.
        /// </summary>
        /// <param name="limit">Requested count or configured admission bound; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>This deliberately unavailable member never returns successfully.</returns>
        /// <exception cref="NotImplementedException">This port member is not implemented by the fixture.</exception>
        public Task<int> DeleteExpiredAsync(int limit, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        ///     Rejects build context because this member is deliberately unavailable in the fixture.
        /// </summary>
        /// <param name="deploymentId">Synthetic deployment identity; ignored by this fixture.</param>
        /// <param name="maximumCharacters">The maximum permitted text length; ignored by this fixture.</param>
        /// <param name="cancellationToken">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>This deliberately unavailable member never returns successfully.</returns>
        /// <exception cref="NotImplementedException">This port member is not implemented by the fixture.</exception>
        public Task<string> BuildContextAsync(Guid deploymentId, int maximumCharacters,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    ///     Simulates a slow transport write that cooperates with cancellation.
    /// </summary>
    private sealed class TestLive : ILogStreamer
    {
        /// <summary>Whether the next terminal write stalls until canceled.</summary>
        public bool StallNext { get; set; }

        /// <summary>Successfully delivered messages.</summary>
        public ConcurrentQueue<DeploymentTerminalLog> Delivered { get; } = new();

        /// <summary>Signals successful delivery of the event after the simulated failure.</summary>
        public TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        ///     Records terminal delivery and optionally stalls until cancellation to exercise recovery.
        /// </summary>
        /// <param name="log">Source-aware redacted terminal record.</param>
        /// <param name="token">Cancellation token for the fixture operation.</param>
        /// <returns>A pending operation that completes through cancellation rather than producing a value.</returns>
        /// <exception cref="OperationCanceledException">The caller cancellation or fixture deadline ends the pending operation.</exception>
        public async Task StreamTerminalLogAsync(DeploymentTerminalLog log, CancellationToken token = default)
        {
            if (StallNext)
            {
                StallNext = false;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            Delivered.Enqueue(log);
            if (log.Message == "password=[REDACTED]") Recovered.TrySetResult();
        }

        /// <summary>
        ///     Completes immediately without executing live stream terminal notice work.
        /// </summary>
        /// <param name="project">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="message">
        ///     Display message; secret-bearing provider text must not be supplied; ignored by this fixture.
        /// </param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>An already completed task; this fixture performs no work.</returns>
        public Task StreamTerminalNoticeAsync(Guid project, string message, CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Completes immediately without executing live stream container metrics work.
        /// </summary>
        /// <param name="project">Synthetic project identity; ignored by this fixture.</param>
        /// <param name="container">Recorded container identity to select; ignored by this fixture.</param>
        /// <param name="cpu">Provider CPU display value; ignored by this fixture.</param>
        /// <param name="memory">Provider memory display value; ignored by this fixture.</param>
        /// <param name="token">Cancellation token for the fixture operation; ignored by this fixture.</param>
        /// <returns>An already completed task; this fixture performs no work.</returns>
        public Task StreamContainerMetricsAsync(Guid project, string container, string cpu, string memory,
            CancellationToken token = default)
        {
            return Task.CompletedTask;
        }
    }
}