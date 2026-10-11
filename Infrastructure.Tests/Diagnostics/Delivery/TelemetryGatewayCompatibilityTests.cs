using System.Net;
using System.Text.Json;
using Application.Abstractions.Diagnostics;
using Infrastructure.Diagnostics;
using Infrastructure.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.Tests.Diagnostics;

/// <summary>Verifies private protocol compatibility independently of tenant history and external storage.</summary>
public sealed class TelemetryGatewayCompatibilityTests
{
    /// <summary>Successful probes are coalesced across reads and empty metrics remain a valid response.</summary>
    [Fact]
    public async Task Compatible_host_reads_empty_metrics_and_caches_probe()
    {
        var probes = 0;
        var reads = 0;
        using var handler = new DelegateHttpMessageHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            if (request.RequestUri!.AbsolutePath == "/capabilities")
            {
                probes++;
                return DelegateHttpMessageHandler.Json(JsonSerializer.Serialize(new TelemetryCapabilities(1,
                    TelemetryCapabilities.RequiredArchiveOperations)));
            }

            reads++;
            Assert.Equal("/archive/metric-batch", request.RequestUri.AbsolutePath);
            return DelegateHttpMessageHandler.Json("{\"items\":[],\"nextOffset\":null}");
        });
        var factory = new StubHttpClientFactory(handler);
        var options = Settings();
        using var compatibility = new TelemetryGatewayCompatibility(factory, options,
            NullLogger<TelemetryGatewayCompatibility>.Instance);
        var gateway = new TelemetryGatewayClient(factory, options, compatibility);
        var range = new MetricTimeRange(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(-1));
        for (var i = 0; i < 2; i++)
            Assert.Empty(
                (await gateway.ReadMetricBatchAsync(new ArchiveMetricBatchRequest(Guid.NewGuid(), range), default))
                .Items);
        Assert.Equal(1, probes);
        Assert.Equal(2, reads);
    }

    /// <summary>Missing routes, rejected server credentials, network faults and incomplete capabilities are distinct.</summary>
    [Theory]
    [InlineData(404, "{}", TelemetryCompatibilityFailure.IncompatibleHost)]
    [InlineData(403, "private-response", TelemetryCompatibilityFailure.AccessDenied)]
    [InlineData(503, "private-response", TelemetryCompatibilityFailure.Unavailable)]
    [InlineData(200, "{\"version\":1,\"archiveOperations\":[\"logs\"]}",
        TelemetryCompatibilityFailure.IncompatibleHost)]
    [InlineData(200, "invalid-private-json", TelemetryCompatibilityFailure.IncompatibleHost)]
    public async Task Failed_probe_is_classified_safely(int status, string body, TelemetryCompatibilityFailure expected)
    {
        using var handler = new DelegateHttpMessageHandler(_ => new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent(body) });
        using var compatibility = new TelemetryGatewayCompatibility(new StubHttpClientFactory(handler), Settings(),
            NullLogger<TelemetryGatewayCompatibility>.Instance);
        var error = await Assert.ThrowsAsync<TelemetryCompatibilityException>(() => compatibility.EnsureAsync(default));
        Assert.Equal(expected, error.Failure);
        Assert.DoesNotContain("private", error.ToString());
        Assert.Null(error.InnerException);
    }

    /// <summary>Network exceptions cannot become empty history or disclose rejected destinations.</summary>
    [Fact]
    public async Task Unreachable_host_is_unavailable()
    {
        using var handler = new DelegateHttpMessageHandler(_ => throw new HttpRequestException("private-url"));
        using var compatibility = new TelemetryGatewayCompatibility(new StubHttpClientFactory(handler), Settings(),
            NullLogger<TelemetryGatewayCompatibility>.Instance);
        var error = await Assert.ThrowsAsync<TelemetryCompatibilityException>(() => compatibility.EnsureAsync(default));
        Assert.Equal(TelemetryCompatibilityFailure.Unavailable, error.Failure);
        Assert.DoesNotContain("private-url", error.ToString());
    }

    /// <summary>A host advertising a missing archive route cannot turn a 404 into an empty metric history.</summary>
    [Fact]
    public async Task Advertised_but_missing_archive_route_is_incompatible()
    {
        using var handler = new DelegateHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath == "/capabilities"
                ? DelegateHttpMessageHandler.Json(JsonSerializer.Serialize(new TelemetryCapabilities(1,
                    TelemetryCapabilities.RequiredArchiveOperations)))
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("private-response") });
        var factory = new StubHttpClientFactory(handler);
        using var compatibility = new TelemetryGatewayCompatibility(factory, Settings(),
            NullLogger<TelemetryGatewayCompatibility>.Instance);
        var gateway = new TelemetryGatewayClient(factory, Settings(), compatibility);
        var range = new MetricTimeRange(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(-1));
        var error = await Assert.ThrowsAsync<TelemetryCompatibilityException>(() =>
            gateway.ReadMetricBatchAsync(new ArchiveMetricBatchRequest(Guid.NewGuid(), range), default));
        Assert.Equal(TelemetryCompatibilityFailure.IncompatibleHost, error.Failure);
        Assert.DoesNotContain("private-response", error.ToString());
    }

    /// <summary>Synthetic private connection settings never point at the user's configured services.</summary>
    private static IOptions<TelemetryStorageOptions> Settings()
    {
        return Options.Create(new TelemetryStorageOptions
            { GatewayUrl = "https://telemetry.example.invalid", GatewayToken = "synthetic-private-credential" });
    }
}