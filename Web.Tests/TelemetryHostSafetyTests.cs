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
using Xunit;

namespace Web.Tests;

/// <summary>Exercises the production private-host composition and authentication over loopback without external stores.</summary>
public sealed class TelemetryHostSafetyTests
{
    /// <summary>Framework URL/header/body/error logs and ordinary providers cross the same safe logging factory as Web.</summary>
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
        });
        Assert.IsType<SafeLoggerFactory>(app.Services.GetRequiredService<ILoggerFactory>());
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var unauthorized = await client.GetAsync("/status?password=private-query");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ingest?password=private-query")
        {
            Content = new StringContent("{password=private-body")
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var badRequest = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, badRequest.StatusCode);
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

    /// <summary>Captures ordinary logger-provider output, including framework calls from the live host.</summary>
    private sealed class CaptureProvider : ILoggerProvider
    {
        /// <summary>Thread-safe serialized snapshots of safe category, message and state.</summary>
        public ConcurrentQueue<object> Entries { get; } = new();

        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName)
        {
            return new CaptureLogger(this, categoryName);
        }

        /// <inheritdoc />
        public void Dispose()
        {
        }

        /// <summary>Observes the exact state passed to a registered provider.</summary>
        private sealed class CaptureLogger(CaptureProvider capture, string category) : ILogger
        {
            /// <inheritdoc />
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return null;
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
                Assert.Null(exception);
                capture.Entries.Enqueue(new { category, message = formatter(state, exception), state });
            }
        }
    }
}