using System.Collections.Concurrent;
using System.Diagnostics;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.Logging;
using Application.Diagnostics;
using Infrastructure.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Diagnostics;

/// <summary>Verifies sink deadlines, durable confirmations and later-event recovery without database payload writes.</summary>
public sealed class DiagnosticDeliveryTests
{
    /// <summary>A stalled write or provider cancellation is isolated from later redacted output.</summary>
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

    /// <summary>Only durable storage confirmation advances a collector cursor, regardless of live delivery success.</summary>
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

    /// <summary>Host shutdown cancels the pending write instead of reporting it as a recoverable storage outage.</summary>
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

    /// <summary>The asynchronous dispatcher restores the collector's trace instead of creating an unrelated root.</summary>
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

    /// <summary>Overflow annotations must not reuse the persisted event identity or metric payload they follow.</summary>
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

    /// <summary>Creates scoped store access without registering EF or a PostgreSQL diagnostic sink.</summary>
    private static ServiceProvider Services(TestStore store, TestLive live)
    {
        return new ServiceCollection().AddSingleton<IDeploymentDiagnosticStore>(store)
            .AddSingleton<ILogStreamer>(live)
            .AddSingleton<IOptions<TelemetryStorageOptions>>(Options.Create(new TelemetryStorageOptions
                { Backend = "LokiMimir", DeliveryMode = "DiskGateway" })).BuildServiceProvider();
    }

    /// <summary>Uses short production-supported deadlines for failure scenarios.</summary>
    private static DeploymentDiagnosticPublisher Publisher(ServiceProvider services)
    {
        return new DeploymentDiagnosticPublisher(new DiagnosticRedactor(),
            Options.Create(
                new DeploymentDiagnosticOptions { PersistenceTimeoutSeconds = 1, DeliveryTimeoutSeconds = 1 }),
            NullLogger<DeploymentDiagnosticPublisher>.Instance, services.GetRequiredService<IServiceScopeFactory>());
    }

    /// <summary>Builds non-sensitive routing metadata; each test assigns its untrusted message separately.</summary>
    private static DeploymentDiagnosticEvent Event()
    {
        return new DeploymentDiagnosticEvent(Guid.NewGuid(), Guid.NewGuid(), DeploymentDiagnosticSource.DockerCompose,
            DeploymentDiagnosticKind.Log, DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow, "fixture",
            new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Build));
    }

    /// <summary>Simulates storage failure once and retains only confirmed, safe events.</summary>
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

        /// <inheritdoc />
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

        /// <inheritdoc />
        public Task<DeploymentTerminalHistory> ReadRecentAsync(Guid projectId, Guid deploymentId, int limit,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public Task<DeploymentTerminalHistory> ReadAfterAsync(Guid projectId, Guid deploymentId, long cursor, int limit,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public Task<int> DeleteExpiredAsync(int limit, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public Task<string> BuildContextAsync(Guid deploymentId, int maximumCharacters,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>Simulates a slow transport write that cooperates with cancellation.</summary>
    private sealed class TestLive : ILogStreamer
    {
        /// <summary>Whether the next terminal write stalls until canceled.</summary>
        public bool StallNext { get; set; }

        /// <summary>Successfully delivered messages.</summary>
        public ConcurrentQueue<DeploymentTerminalLog> Delivered { get; } = new();

        /// <summary>Signals successful delivery of the event after the simulated failure.</summary>
        public TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
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

        /// <inheritdoc />
        public Task StreamTerminalNoticeAsync(Guid project, string message, CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StreamContainerMetricsAsync(Guid project, string container, string cpu, string memory,
            CancellationToken token = default)
        {
            return Task.CompletedTask;
        }
    }
}