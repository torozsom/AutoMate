using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Infrastructure.Observability;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telemetry;

namespace Test.Web.Hosting;

/// <summary>
///     Exercises the production private-host composition and authentication over loopback without
///     external stores.
/// </summary>
public sealed class TelemetryHostSafetyTests
{
    /// <summary>
    ///     Framework URL/header/body/error logs and ordinary providers cross the same safe logging
    ///     factory as Web.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Private_host_masks_logging_while_preserving_authentication_and_bad_request_status()
    {
        const string token = "synthetic-private-credential-1234567890";
        var capture = new CaptureProvider();
        await using var app = TelemetryApplication.Build([], builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TelemetryStorage:Backend"] = "LokiMimir",
                ["TelemetryStorage:DeliveryMode"] = "DiskGateway",
                ["TelemetryStorage:GatewayUrl"] = "https://gateway.example.invalid",
                ["TelemetryStorage:GatewayToken"] = token,
                ["TelemetryStorage:LokiUrl"] = "https://logs.example.invalid",
                ["TelemetryStorage:MetricsWriteUrl"] = "https://metrics.example.invalid/otlp/v1/metrics",
                ["TelemetryStorage:MetricsQueryUrl"] = "https://metrics.example.invalid/prometheus"
            });
            // Isolate network dependencies; retain the actual production routes, options and logging registrations.
            builder.Services.RemoveAll<IHostedService>();
            builder.Logging.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(capture);
            builder.Services.AddSafePlatformLogging();
            builder.Services.AddHttpClient("Loopback");
        });

        Assert.IsType<SafeLoggerFactory>(app.Services.GetRequiredService<ILoggerFactory>());

        await app.StartAsync();
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("Loopback");
        client.BaseAddress = new Uri(app.Urls.Single());
        using var unauthorized = await client.GetAsync("/status?password=private-query");

        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var anonymousCapabilities = await client.GetAsync("/capabilities");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousCapabilities.StatusCode);

        using var capabilityRequest = new HttpRequestMessage(HttpMethod.Get, "/capabilities");

        capabilityRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var capabilityResponse = await client.SendAsync(capabilityRequest);

        Assert.Equal(HttpStatusCode.OK, capabilityResponse.StatusCode);

        var capabilityJson = await capabilityResponse.Content.ReadAsStringAsync();

        Assert.Contains("assessment-metrics", capabilityJson);
        Assert.Contains("metric-batch", capabilityJson);
        Assert.DoesNotContain(token, capabilityJson);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/ingest?password=private-query")
            ;
        request.Content = new StringContent("{password=private-body");

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var badRequest = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, badRequest.StatusCode);

        using var missingCredential = await client.PostAsync("/archive/metric-batch",
            new StringContent("{}"));

        Assert.Equal(HttpStatusCode.Unauthorized, missingCredential.StatusCode);

        using var malformedBatch = new HttpRequestMessage(HttpMethod.Post, "/archive/metric-batch")
            ;
        malformedBatch.Content = new StringContent("{password=private-body");

        malformedBatch.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        malformedBatch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var rejectedBatch = await client.SendAsync(malformedBatch);

        Assert.Equal(HttpStatusCode.BadRequest, rejectedBatch.StatusCode);

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("private-category");

        using (logger.BeginScope(new Dictionary<string, object?>
                   { ["Authorization"] = token, ["DeploymentId"] = Guid.NewGuid() }))
        {
            logger.LogError(new InvalidOperationException("password=private-exception"), "private-message {Url}",
                "https://private-host.invalid");
        }

        await app.StopAsync();

        Assert.NotEmpty(capture.Entries);

        var exported = JsonSerializer.Serialize(capture.Entries);

        Assert.DoesNotContain("private-", exported);
        Assert.DoesNotContain(token, exported);
        Assert.Contains(PlatformTelemetryPolicy.UnknownMessage, exported);
    }

    /// <summary>
    ///     Captures ordinary logger-provider output, including framework calls from the live host.
    /// </summary>
    private sealed class CaptureProvider : ILoggerProvider
    {
        /// <summary>Thread-safe serialized snapshots of safe category, message and state.</summary>
        public ConcurrentQueue<object> Entries { get; } = new();

        /// <summary>
        ///     Creates a logger writing to the fixture capture provider.
        /// </summary>
        /// <param name="categoryName">Logger category reviewed before platform output.</param>
        /// <returns>A logger attached to the fixture capture provider.</returns>
        public ILogger CreateLogger(string categoryName)
        {
            return new CaptureLogger(this, categoryName);
        }

        /// <summary>
        ///     Performs no dispose work in this fixture.
        /// </summary>
        public void Dispose()
        {
        }

        /// <summary>
        ///     Observes the exact state passed to a registered provider.
        /// </summary>
        /// <param name="capture">Logger provider retaining safe snapshots for assertions.</param>
        /// <param name="category">Reviewed logger category or provider diagnostic category.</param>
        private sealed class CaptureLogger(CaptureProvider capture, string category) : ILogger
        {
            /// <summary>
            ///     Returns no scope handle because this fixture does not retain scopes.
            /// </summary>
            /// <param name="state">Synthetic readiness or structured logger state; ignored by this fixture.</param>
            /// <typeparam name="TState">Type of structured logger state.</typeparam>
            /// <returns>Null; this fixture retains no scopes.</returns>
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return null;
            }

            /// <summary>
            ///     Enables every log level so the fixture can observe all emitted records.
            /// </summary>
            /// <param name="level">Reviewed log severity used to select safe output; ignored by this fixture.</param>
            /// <returns>True for every call in this fixture.</returns>
            public bool IsEnabled(LogLevel level)
            {
                return true;
            }

            /// <summary>
            ///     Captures or checks the logger call using the fixture assertions and supplied formatter.
            /// </summary>
            /// <param name="level">Reviewed log severity used to select safe output; ignored by this fixture.</param>
            /// <param name="eventId">The stable event identity used to deduplicate retries; ignored by this fixture.</param>
            /// <param name="state">Synthetic readiness or structured logger state.</param>
            /// <param name="exception">Connection failure, or null for a normal disconnect.</param>
            /// <param name="formatter">Provider log formatter; untrusted rendered text is not exported.</param>
            /// <typeparam name="TState">Type of structured logger state.</typeparam>
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                Assert.Null(exception);

                capture.Entries.Enqueue(new { category, message = formatter(state, exception), state });
            }
        }
    }
}
