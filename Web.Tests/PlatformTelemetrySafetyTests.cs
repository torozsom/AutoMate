using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Application.Diagnostics;
using Infrastructure.Diagnostics;
using Infrastructure.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;
using Web.Observability;
using Xunit;

namespace Web.Tests;

/// <summary>Exercises policy at actual provider/export boundaries with adversarial untrusted metadata.</summary>
public sealed class PlatformTelemetrySafetyTests
{
    /// <summary>Actual HttpClientFactory framework events retain safe request/response details through the host factory.</summary>
    [Fact]
    public async Task Real_http_client_logging_preserves_framework_summaries_without_request_urls()
    {
        using var capture = new CaptureProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(capture));
        services.AddSafePlatformLogging();
        services.AddHttpClient("Default").ConfigurePrimaryHttpMessageHandler(() => new SafeHttpFixture());
        await using var provider = services.BuildServiceProvider();
        using var response = await provider.GetRequiredService<IHttpClientFactory>().CreateClient("Default")
            .PostAsync("https://private.invalid?token=private-query", new StringContent("private-body"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var text = JsonSerializer.Serialize(capture.Records);
        Assert.Contains("method POST", text);
        Assert.Contains("status 403", text);
        Assert.Contains("duration", text);
        Assert.DoesNotContain("private", text);
        Assert.DoesNotContain("additional text fields withheld", text);
    }

    /// <summary>Returns a synthetic rejection without network access.</summary>
    private sealed class SafeHttpFixture : HttpMessageHandler
    {
        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
    }

    /// <summary>HTTP summaries retain method, timing, status and source while excluding URLs and provider payloads.</summary>
    [Fact]
    public void Framework_requests_and_authentication_keep_detailed_safe_operation_metadata()
    {
        using var capture = new CaptureProvider();
        using var inner = LoggerFactory.Create(logging => logging.AddProvider(capture));
        using var factory = new SafeLoggerFactory(inner, new PlatformTelemetryPolicy(new DiagnosticRedactor()));
        var client = factory.CreateLogger("System.Net.Http.HttpClient.IGitHubService.ClientHandler");
        client.LogInformation("Sending HTTP request {HttpMethod} {Uri}", "POST", "https://private.invalid?token=private-query");
        client.LogInformation("Received HTTP response headers after {ElapsedMilliseconds}ms - {StatusCode}", 42.5, 401);
        factory.CreateLogger("Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationHandler")
            .LogInformation("AuthenticationScheme: {AuthenticationScheme} signed in.", "Cookies");
        factory.CreateLogger("AppStartup").LogInformation("[Startup] Database migrations are current.");
        var text = JsonSerializer.Serialize(capture.Records);
        Assert.Contains("IGitHubService.ClientHandler", text);
        Assert.Contains("method POST", text);
        Assert.Contains("status 401", text);
        Assert.Contains("duration 42.5 ms", text);
        Assert.Contains("Cookies signed in", text);
        Assert.Contains("AppStartup", text);
        Assert.DoesNotContain("private", text);
        Assert.DoesNotContain(PlatformTelemetryPolicy.UnknownMessage, text);
    }

    /// <summary>Reviewed lifecycle summaries retain operational meaning without exporting host addresses or paths.</summary>
    [Fact]
    public void Lifecycle_and_compose_summaries_remain_searchable_without_unmasking_sensitive_values()
    {
        using var capture = new CaptureProvider();
        using var inner = LoggerFactory.Create(logging => logging.AddProvider(capture));
        using var factory = new SafeLoggerFactory(inner, new PlatformTelemetryPolicy(new DiagnosticRedactor()));
        var logger = factory.CreateLogger("Microsoft.Hosting.Lifetime");
        logger.LogInformation("Now listening on: {address}", "https://private-host.invalid:443");
        logger.LogInformation("Content root path: {ContentRoot}", "C:/private-root");
        logger.LogInformation("Application is shutting down...");
        logger.LogInformation("Docker Compose command outcome for project {ProjectId}: {ComposeOutcome}.",
            Guid.NewGuid(), "timed-out");
        logger.LogInformation("Database dependency detected for project {ProjectId}: {DbType}.",
            Guid.NewGuid(), "PostgreSQL");
        logger.LogInformation("Database dependency detected for project {ProjectId}: {DbType}.",
            Guid.NewGuid(), "private-provider");
        var text = JsonSerializer.Serialize(capture.Records);
        Assert.Contains("HTTP listener started.", text);
        Assert.Contains("Content root initialized.", text);
        Assert.Contains("Application is shutting down.", text);
        Assert.Contains("timed-out", text);
        Assert.Contains("PostgreSQL", text);
        Assert.DoesNotContain("private", text);
        Assert.DoesNotContain(PlatformTelemetryPolicy.UnknownMessage, text);
    }

    /// <summary>Both ordinary providers and SDK exports receive safe detached state/scopes, preserving audit correlation.</summary>
    [Fact]
    public void Log_providers_never_receive_payloads_exception_objects_or_unapproved_scope_values()
    {
        var policy = new PlatformTelemetryPolicy(new DiagnosticRedactor());
        using var exporter = new SnapshotExporter();
        using var capture = new CaptureProvider();
        using var inner = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(capture);
            logging.AddOpenTelemetry(options =>
            {
                options.IncludeScopes = true;
                options.IncludeFormattedMessage = true;
                options.AddProcessor(new SafeLogProcessor(policy));
                options.AddProcessor(new SimpleLogRecordExportProcessor(exporter));
            });
        });
        using var factory = new SafeLoggerFactory(inner, policy);
        var logger = factory.CreateLogger("private-category-token");
        var project = Guid.NewGuid();
        var scope = new Dictionary<string, object?>
        { ["ProjectId"] = project, ["Cookie"] = "private-cookie", ["private-key"] = new NeverFormat() };
        using (logger.BeginScope(scope))
        {
            scope["ProjectId"] = Guid.NewGuid();
            scope["Cookie"] = "private-mutated";
            OperationalLog.Record(logger, AuditOperation.Authorization, AuditOutcome.Denied);
            logger.LogError(new EventId(7, "private-event-name"), new Exception("private-exception"),
                "Failed to save GitHub repository {RepositoryUrl}.", "https://private.example.invalid/token");
            logger.LogInformation("private-interpolated-message");
            logger.Log(LogLevel.Warning, new EventId(8), new NeverFormat(), new Exception("private-exception"),
                (_, _) => throw new InvalidOperationException("Caller formatter must not be invoked."));
        }

        Assert.Equal(4, capture.Records.Count);
        Assert.Equal(4, exporter.Records.Count);
        Assert.All(capture.Records, record => Assert.DoesNotContain("private", JsonSerializer.Serialize(record)));
        Assert.All(exporter.Records, record =>
        {
            Assert.Null(record.Exception);
            Assert.Null(record.EventId.Name);
            Assert.DoesNotContain("private", JsonSerializer.Serialize(record));
            Assert.Equal(project, record.Scopes["ProjectId"]);
        });
        var audit = Assert.Single(exporter.Records, record => record.Attributes.ContainsKey("Operation"));
        Assert.Equal("Authorization", audit.Attributes["Operation"]);
        Assert.Equal("Denied", audit.Attributes["Outcome"]);
    }

    /// <summary>Unsupported immutable events/links and excessive tags are suppressed for both SDK export processor types.</summary>
    [Theory]
    [InlineData(false, "event")]
    [InlineData(true, "event")]
    [InlineData(false, "link")]
    [InlineData(true, "link")]
    [InlineData(false, "tags")]
    public void Trace_payloads_fail_closed_before_simple_or_batch_export(bool batch, string payload)
    {
        using var exporter = new PrivacyTraceExporter();
        var builder = Sdk.CreateTracerProviderBuilder().AddSource("privacy.fixture")
            .AddProcessor(new SafeTraceProcessor(new PlatformTelemetryPolicy(new DiagnosticRedactor())));
        builder.AddProcessor(batch
            ? new BatchActivityExportProcessor(exporter)
            : new SimpleActivityExportProcessor(exporter));
        using var provider = builder.Build();
        using var source = new ActivitySource("privacy.fixture");
        var links = payload == "link"
            ? new[] { new ActivityLink(default, new ActivityTagsCollection { ["private-key"] = "private-link" }) }
            : null;
        using (var activity = source.StartActivity("private-operation", ActivityKind.Client, default(ActivityContext),
                   links: links))
        {
            Assert.NotNull(activity);
            if (payload == "event")
                activity.AddEvent(new ActivityEvent("private-event",
                    tags: new ActivityTagsCollection { ["body"] = "private-value" }));
            if (payload == "tags")
                for (var i = 0; i < 129; i++)
                    activity.SetTag("private-tag-" + i, "private-value");
        }

        Assert.True(provider.ForceFlush());
        Assert.Empty(exporter.Records);
    }

    /// <summary>Real OTLP serialization omits HTTP/SQL/baggage/status/name payloads while retaining status and IDs.</summary>
    [Fact]
    public async Task Otlp_wire_contains_safe_http_status_and_correlation_without_payloads()
    {
        var received = new ConcurrentQueue<byte[]>();
        var appBuilder = WebApplication.CreateBuilder();
        appBuilder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        appBuilder.Logging.ClearProviders();
        await using var app = appBuilder.Build();
        app.MapPost("/{**path}", async (HttpContext context) =>
        {
            using var bytes = new MemoryStream();
            await context.Request.Body.CopyToAsync(bytes);
            received.Enqueue(bytes.ToArray());
            return Results.Bytes([], "application/x-protobuf");
        });
        await app.StartAsync();
        using var provider = Sdk.CreateTracerProviderBuilder().AddSource("privacy.wire")
            .AddProcessor(new SafeTraceProcessor(new PlatformTelemetryPolicy(new DiagnosticRedactor())))
            .AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(app.Urls.Single());
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
                options.ExportProcessorType = ExportProcessorType.Simple;
            }).Build();
        using var source = new ActivitySource("privacy.wire");
        var project = Guid.NewGuid();
        ActivityTraceId traceId;
        using (var activity = source.StartActivity("private-span-name", ActivityKind.Server))
        {
            Assert.NotNull(activity);
            traceId = activity.TraceId;
            activity.DisplayName = "private-display-name";
            activity.SetTag("deployment.project.id", project);
            activity.SetTag("http.response.status_code", 503);
            activity.SetTag("http.request.method", "GET");
            activity.SetTag("url.full", "https://private.example.invalid/?token=private-query");
            activity.SetTag("db.query.text", "select 'private-sql'");
            activity.SetTag("private-unknown-key", "private-value");
            activity.AddBaggage("private-baggage-key", "private-baggage");
            activity.TraceStateString = "vendor=private-state";
            activity.SetStatus(ActivityStatusCode.Error, "private-error-description");
        }

        Assert.True(provider.ForceFlush());
        var packet = Assert.Single(received);
        var decoded = Encoding.UTF8.GetString(packet);
        Assert.DoesNotContain("private", decoded);
        Assert.Contains("http.server.request", decoded);
        Assert.Contains("http.response.status_code", decoded);
        Assert.Contains(project.ToString(), decoded);
        Assert.True(packet.AsSpan().IndexOf(Convert.FromHexString(traceId.ToString())) >= 0);
    }

    /// <summary>Unknown external objects must never have their formatter invoked.</summary>
    private sealed class NeverFormat
    {
        /// <inheritdoc />
        public override string ToString()
        {
            throw new InvalidOperationException("Untrusted formatter invoked.");
        }
    }

    /// <summary>Copies safe metadata as the actual trace exporter sees it.</summary>
    private sealed class PrivacyTraceExporter : BaseExporter<Activity>
    {
        /// <summary>Exported detached data.</summary>
        public ConcurrentQueue<string> Records { get; } = new();

        /// <inheritdoc />
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
                Records.Enqueue(JsonSerializer.Serialize(new
                {
                    activity.DisplayName,
                    activity.StatusDescription,
                    activity.TraceStateString,
                    Tags = activity.TagObjects.ToArray(),
                    Baggage = activity.Baggage.ToArray()
                }));
            return ExportResult.Success;
        }
    }

    /// <summary>Captures the state reaching an ordinary non-OTel logger provider.</summary>
    private sealed class CaptureProvider : ILoggerProvider, ISupportExternalScope
    {
        /// <summary>Factory-owned shared scopes.</summary>
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        /// <summary>Detached provider observations.</summary>
        public ConcurrentQueue<object> Records { get; } = new();

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName)
        {
            return new CaptureLogger(this, categoryName);
        }

        /// <inheritdoc />
        public void Dispose()
        {
        }

        /// <inheritdoc />
        public void SetScopeProvider(IExternalScopeProvider scopeProvider)
        {
            _scopes = scopeProvider;
        }

        /// <summary>Receives exactly the state/formatter that standard console providers receive.</summary>
        private sealed class CaptureLogger(CaptureProvider owner, string category) : ILogger
        {
            /// <inheritdoc />
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return owner._scopes.Push(state);
            }

            /// <inheritdoc />
            public bool IsEnabled(LogLevel level)
            {
                return true;
            }

            /// <inheritdoc />
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var scopes = new List<object?>();
                owner._scopes.ForEachScope((scope, list) => list.Add(scope), scopes);
                owner.Records.Enqueue(new
                { category, eventId, exception, Message = formatter(state, exception), state, scopes });
            }
        }
    }
}
