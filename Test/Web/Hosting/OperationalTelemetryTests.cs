using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Application.Abstractions.Diagnostics.Events;
using Application.Diagnostics;
using Infrastructure.Diagnostics.Policy;
using Infrastructure.Diagnostics.Storage;
using Infrastructure.Observability;
using Infrastructure.Observability.OpenTelemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using DiagnosticRedactor = Infrastructure.Diagnostics.Redaction.DiagnosticRedactor;

namespace Test.Web.Hosting;

/// <summary>
///     Exercises actual export ordering, finite HTTP observations and shared-host wire resources.
/// </summary>
[Collection("Operational telemetry isolation")]
public sealed class OperationalTelemetryTests
{
    /// <summary>
    ///     Warning cooldowns use monotonic time and invalid emitter values cannot create arbitrary metric labels.
    /// </summary>
    [Fact]
    public void Cooldowns_and_operation_labels_remain_finite()
    {
        var clock = new FakeTimeProvider();
        var limiter = new OperationalWarningLimiter(clock);
        Assert.True(limiter.Admit(ReviewedHttpClient.AzureRuntime, "slow"));
        Assert.False(limiter.Admit(ReviewedHttpClient.AzureRuntime, "slow"));
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(limiter.Admit(ReviewedHttpClient.AzureRuntime, "slow"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(limiter.Admit(ReviewedHttpClient.AzureRuntime, "slow"));
        var observed = new List<KeyValuePair<string, object?>[]>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Name == "automate.operation.count") owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => observed.Add(tags.ToArray()));
        listener.Start();
        OperationalMetrics.Record("password=private", "https://private", "private", Stopwatch.GetTimestamp(),
            "private");
        var fields = Assert.Single(observed).ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal("Unknown", fields["source"]);
        Assert.Equal("Unknown", fields["stage"]);
        Assert.Equal("Unknown", fields["outcome"]);
        Assert.Equal("none", fields["status_class"]);
    }

    /// <summary>
    ///     Disabled export still rejects nonfinite rates and nonpositive slow thresholds.
    /// </summary>
    [Fact]
    public void Volume_configuration_rejects_invalid_rates_and_thresholds()
    {
        var validator = new OpenTelemetryOptionsValidator();
        foreach (var rate in new[] { -0.1, 1.1, double.NaN, double.PositiveInfinity })
            Assert.False(validator.Validate(null, new OpenTelemetryOptions { SignalRSuccessSampleRate = rate })
                .Succeeded);
        Assert.False(validator.Validate(null, new OpenTelemetryOptions { ClaimSlowMilliseconds = 0 }).Succeeded);
        Assert.False(validator.Validate(null, new OpenTelemetryOptions { SignalRSlowMilliseconds = -1 }).Succeeded);
        Assert.True(validator.Validate(null, new OpenTelemetryOptions()).Succeeded);
    }

    /// <summary>
    ///     A stalled exporter and a saturated span queue cannot hold durable spool receipts.
    /// </summary>
    /// <returns>Completion after isolated queue pressure and temporary-spool verification.</returns>
    [Fact]
    public async Task Exporter_outage_does_not_block_durable_receipts_or_operations()
    {
        var directory = Path.Combine(Path.GetTempPath(), "automate-export-outage-" + Guid.NewGuid().ToString("N"));
        using var transport = new HeldExporter();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["OpenTelemetry:OtlpEndpoint"] = "http://collector.invalid:4318" });
        builder.AddSafeOpenTelemetry("AutoMate.Telemetry");
        builder.Services.PostConfigureAll<OtlpExporterOptions>(transport.Configure);
        using var host = builder.Build();
        await host.StartAsync();
        _ = host.Services.GetRequiredService<TracerProvider>();
        try
        {
            var watch = Stopwatch.StartNew();
            for (var index = 0; index < 4000; index++)
            {
                using var activity = AutoMateTelemetry.Deployments.StartActivity("deployment.diagnostic.persist",
                    ActivityKind.Internal, default(ActivityContext));
                Assert.NotNull(activity);
                DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Completed);
            }

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10),
                "Operation completion must not wait for exporter I/O.");
            await transport.Requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var spool = new DiskTelemetrySpool(Options.Create(new DiskSpoolOptions { Directory = directory }),
                Options.Create(new TelemetryStorageOptions()), NullLogger<DiskTelemetrySpool>.Instance,
                new DiagnosticRedactor());
            await spool.StartAsync(CancellationToken.None);
            for (var index = 0; index < 3; index++)
            {
                var observation = new DeploymentDiagnosticEvent(Guid.NewGuid(), Guid.NewGuid(),
                    DeploymentDiagnosticSource.DockerContainer, DeploymentDiagnosticKind.Log,
                    DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow, "synthetic observation",
                    new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, "web"),
                    EventId: Guid.NewGuid());
                await spool.AppendAsync(Guid.NewGuid(), observation, "web", CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }

            Assert.Equal(3, (await spool.SnapshotAsync(CancellationToken.None)).PendingEvents);
            Assert.False(transport.Release.Task.IsCompleted);
            await spool.StopAsync(CancellationToken.None);
        }
        finally
        {
            transport.Release.TrySetResult();
            await host.StopAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>
    ///     Export processors honor end-of-operation filtering while slow/error/canceled spans survive.
    /// </summary>
    /// <param name="batch">Whether export uses the asynchronous SDK processor.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Volume_filter_retains_slow_failed_and_canceled_operations(bool batch)
    {
        using var exporter = new SpanExporter();
        var builder = Sdk.CreateTracerProviderBuilder().AddSource("AutoMate.Deployments", "AutoMate.Analysis")
            .AddProcessor(new SafeTraceProcessor(new PlatformTelemetryPolicy(new DiagnosticRedactor())))
            .AddProcessor(new OperationalTraceFilter(new OpenTelemetryOptions
            {
                SignalRSuccessSampleRate = 0,
                EmptyClaimSampleRate = 0,
                SignalRSlowMilliseconds = 100,
                ClaimSlowMilliseconds = 250
            }));
        builder.AddProcessor(batch
            ? new BatchActivityExportProcessor(exporter)
            : new SimpleActivityExportProcessor(exporter));
        using var provider = builder.Build();
        using var source = new ActivitySource("AutoMate.Deployments");
        foreach (var outcome in new[] { "completed", "failed", "timed_out", "canceled" })
        {
            using var activity = source.StartActivity("deployment.signalr.send", ActivityKind.Internal,
                default(ActivityContext));
            Assert.NotNull(activity);
            activity.SetTag("deployment.outcome", outcome);
            if (outcome == "failed") activity.SetStatus(ActivityStatusCode.Error);
        }

        using (var slow = source.StartActivity("deployment.signalr.send", ActivityKind.Internal,
                   default(ActivityContext), startTime: DateTimeOffset.UtcNow.AddSeconds(-1)))
        {
            slow!.SetTag("deployment.outcome", "completed");
        }

        using (var privacy =
               source.StartActivity("deployment.signalr.send", ActivityKind.Internal, default(ActivityContext)))
        {
            privacy!.SetTag("deployment.outcome", "failed");
            privacy.AddEvent(new ActivityEvent("private-payload"));
        }

        using var analysis = new ActivitySource("AutoMate.Analysis");
        using (var empty = analysis.StartActivity("analysis.claim", ActivityKind.Internal, default(ActivityContext)))
        {
            empty!.SetTag("analysis.outcome", "empty");
        }

        Assert.True(provider.ForceFlush());
        Assert.Equal(4, exporter.Spans.Count);
        Assert.Contains("failed", exporter.Spans);
        Assert.Contains("timed_out", exporter.Spans);
        Assert.Contains("canceled", exporter.Spans);
        Assert.Contains("completed", exporter.Spans);
    }

    /// <summary>
    ///     Sampling is repeatable, respects edge rates and approximates the default one-percent selection.
    /// </summary>
    [Fact]
    public void Span_identity_selection_is_deterministic_and_bounded()
    {
        var retained = 0;
        for (var index = 1; index <= 10000; index++)
        {
            var id = ActivitySpanId.CreateFromString(index.ToString("x16"));
            var decision = OperationalTraceFilter.Retain(id, 0.01);
            Assert.Equal(decision, OperationalTraceFilter.Retain(id, 0.01));
            Assert.False(OperationalTraceFilter.Retain(id, 0));
            Assert.True(OperationalTraceFilter.Retain(id, 1));
            if (decision) retained++;
        }

        Assert.InRange(retained, 50, 150);
    }

    /// <summary>
    ///     Both host registrations export the same safe per-process identity across all three OTLP signals.
    /// </summary>
    /// <returns>A task completing after an ephemeral loopback receiver observes both hosts.</returns>
    [Fact]
    public async Task Shared_hosts_export_distinct_service_names_and_consistent_resources()
    {
        var packets = new ConcurrentQueue<(string Path, string Data)>();
        var receiverBuilder = WebApplication.CreateBuilder();
        receiverBuilder.Logging.ClearProviders();
        receiverBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var receiver = receiverBuilder.Build();
        receiver.MapPost("/{**path}", async (HttpContext context) =>
        {
            using var memory = new MemoryStream();
            await context.Request.Body.CopyToAsync(memory);
            packets.Enqueue((context.Request.Path, Encoding.UTF8.GetString(memory.ToArray())));
            return Results.Bytes([], "application/x-protobuf");
        });
        await receiver.StartAsync();
        foreach (var service in new[] { "AutoMate", "AutoMate.Telemetry" })
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenTelemetry:OtlpEndpoint"] = receiver.Urls.Single(),
                ["OpenTelemetry:Environment"] = "Testing"
            });
            builder.AddSafeOpenTelemetry(service);
            builder.AddSafeOpenTelemetry("must-not-register-twice");
            builder.Services.ConfigureAll<OtlpExporterOptions>(o => o.Protocol = OtlpExportProtocol.HttpProtobuf);
            using var host = builder.Build();
            await host.StartAsync();
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AutoMate.Platform")
                .LogInformation("Application started.");
            using (var span = AutoMateTelemetry.Deployments.StartActivity("deployment.diagnostic.persist",
                       ActivityKind.Internal, default(ActivityContext)))
            {
                DeploymentTracing.Finish(span, DeploymentTraceOutcome.Completed);
            }

            OperationalMetrics.Record("AutoMate", "Persistence", "completed", Stopwatch.GetTimestamp());
            Assert.True(host.Services.GetRequiredService<TracerProvider>().ForceFlush());
            Assert.True(host.Services.GetRequiredService<MeterProvider>().ForceFlush());
            Assert.True(host.Services.GetRequiredService<LoggerProvider>().ForceFlush());
            var resources = host.Services.GetRequiredService<TracerProvider>().GetResource().Attributes
                .ToDictionary(p => p.Key, p => p.Value);
            var instance = resources["service.instance.id"].ToString()!;
            Assert.True(Guid.TryParse(instance, out _));
            foreach (var signal in new[] { "/v1/traces", "/v1/metrics", "/v1/logs" })
            {
                for (var attempt = 0;
                     attempt < 500 && !packets.Any(p =>
                         p.Path == signal && p.Data.Contains(service) && p.Data.Contains(instance));
                     attempt++)
                    await Task.Delay(20);
                Assert.True(packets.Any(p => p.Path == signal && p.Data.Contains(service) && p.Data.Contains(instance)),
                    $"Missing {signal} for {service}; packets: {string.Join(", ", packets.Select(p => $"{p.Path} service={p.Data.Contains(service)} instance={p.Data.Contains(instance)}"))}");
            }

            await host.StopAsync();
        }

        Assert.DoesNotContain(packets, p => p.Data.Contains("must-not-register-twice"));
    }

    /// <summary>
    ///     Default HTTP noise is suppressed, explicit overrides work, and repeated failures retain safe context.
    /// </summary>
    /// <returns>Completion after synthetic failed requests.</returns>
    [Fact]
    public async Task Http_filters_and_warning_limits_preserve_safe_failure_context()
    {
        var capture = new LogCapture();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(capture));
        services.AddSafePlatformLogging();
        services.AddHttpClient("AzureRuntime").ObserveOperations(ReviewedHttpClient.AzureRuntime)
            .ConfigurePrimaryHttpMessageHandler(() => new FailedHttpHandler());
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ILoggerFactory>();
        Assert.False(factory.CreateLogger("System.Net.Http.HttpClient.AzureRuntime.LogicalHandler")
            .IsEnabled(LogLevel.Information));
        Assert.False(factory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics").IsEnabled(LogLevel.Information));
        Assert.False(factory.CreateLogger("Microsoft.AspNetCore.Routing.EndpointMiddleware")
            .IsEnabled(LogLevel.Information));
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("AzureRuntime");
        for (var i = 0; i < 3; i++)
        {
            using var response = await client.GetAsync("https://private.invalid/?token=private-secret");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        var warning = Assert.Single(capture.Messages);
        Assert.Contains("AzureRuntime", warning);
        Assert.Contains("unsuccessful", warning);
        Assert.Contains("5xx", warning);
        Assert.Contains("status 503", warning);
        Assert.DoesNotContain("private", warning);
        Assert.DoesNotContain(PlatformTelemetryPolicy.UnknownMessage, warning);
    }

    /// <summary>
    ///     Stalls synthetic exporter I/O until the test permits it, then returns an outage response.
    /// </summary>
    private sealed class HeldExporter : HttpMessageHandler
    {
        /// <summary>Signals that the batch worker reached the backend boundary.</summary>
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Controls the simulated backend outage.</summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        ///     Configures exporter-owned clients while the fixture retains transport ownership through host shutdown.
        /// </summary>
        /// <param name="options">Named exporter options used inside this fixture's host lifetime.</param>
        public void Configure(OtlpExporterOptions options)
        {
            options.Protocol = OtlpExportProtocol.HttpProtobuf;
            options.HttpClientFactory = CreateClient;
            options.TimeoutMilliseconds = 1000;
        }

        /// <summary>
        ///     Creates a client whose disposal cannot prematurely release the fixture-owned transport.
        /// </summary>
        /// <returns>An exporter-owned HTTP client.</returns>
        private HttpClient CreateClient()
        {
            return new HttpClient(this, false);
        }

        /// <summary>
        ///     Implements the SDK's synchronous HTTP export boundary on its background worker.
        /// </summary>
        /// <param name="request">Unused exporter envelope.</param>
        /// <param name="cancellationToken">Exporter timeout cancellation.</param>
        /// <returns>A synthetic outage response after release.</returns>
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.TrySetResult();
            Release.Task.Wait(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        /// <summary>
        ///     Holds only the exporter thread, honoring its timeout cancellation.
        /// </summary>
        /// <param name="request">Unused exporter envelope.</param>
        /// <param name="cancellationToken">Exporter timeout cancellation.</param>
        /// <returns>A synthetic unavailable response after release.</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requested.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }

    /// <summary>
    ///     Captures detached approved outcome strings before activity objects are reused.
    /// </summary>
    private sealed class SpanExporter : BaseExporter<Activity>
    {
        /// <summary>Exported outcomes.</summary>
        public ConcurrentQueue<string> Spans { get; } = new();

        /// <summary>
        ///     Copies only the approved deployment outcome.
        /// </summary>
        /// <param name="batch">Completed activities.</param>
        /// <returns>Successful fixture export.</returns>
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
                Spans.Enqueue(activity.GetTagItem("deployment.outcome") as string ?? "unknown");
            return ExportResult.Success;
        }
    }

    /// <summary>
    ///     Returns a synthetic failure without making network requests.
    /// </summary>
    private sealed class FailedHttpHandler : HttpMessageHandler
    {
        /// <summary>
        ///     Returns a service-unavailable response with private content which instrumentation must ignore.
        /// </summary>
        /// <param name="request">Unused fixture request.</param>
        /// <param name="cancellationToken">Unused fixture cancellation.</param>
        /// <returns>The synthetic response.</returns>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                { Content = new StringContent("private-body") });
        }
    }

    /// <summary>
    ///     Captures already-sanitized operator messages.
    /// </summary>
    private sealed class LogCapture : ILoggerProvider
    {
        /// <summary>Thread-safe captured messages.</summary>
        public ConcurrentQueue<string> Messages { get; } = new();

        /// <summary>
        ///     Creates a capture logger without retaining categories.
        /// </summary>
        /// <param name="categoryName">Unused reviewed category.</param>
        /// <returns>The capture logger.</returns>
        public ILogger CreateLogger(string categoryName)
        {
            return new CaptureLogger(Messages);
        }

        /// <summary>
        ///     Releases no external fixture resources.
        /// </summary>
        public void Dispose()
        {
        }
    }

    /// <summary>
    ///     Captures formatted safe state without inspecting original requests.
    /// </summary>
    /// <param name="messages">Owned message queue.</param>
    private sealed class CaptureLogger(ConcurrentQueue<string> messages) : ILogger
    {
        /// <summary>
        ///     Accepts no fixture scopes.
        /// </summary>
        /// <param name="state">Unused scope.</param>
        /// <typeparam name="TState">Scope state type.</typeparam>
        /// <returns>No owned scope.</returns>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        /// <summary>
        ///     Leaves filtering to the production logger factory.
        /// </summary>
        /// <param name="logLevel">Requested severity.</param>
        /// <returns>True for every severity.</returns>
        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        /// <summary>
        ///     Stores only production-sanitized formatted state.
        /// </summary>
        /// <param name="logLevel">Severity.</param>
        /// <param name="eventId">Reviewed event identity.</param>
        /// <param name="state">Sanitized state.</param>
        /// <param name="exception">Sanitized exception slot.</param>
        /// <param name="formatter">Production formatter.</param>
        /// <typeparam name="TState">Sanitized state type.</typeparam>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            messages.Enqueue(formatter(state, exception));
        }
    }
}

/// <summary>
///     Isolates process-wide ActivitySource pressure tests from other exporter fixtures.
/// </summary>
[CollectionDefinition("Operational telemetry isolation", DisableParallelization = true)]
public sealed class OperationalTelemetryCollection;
