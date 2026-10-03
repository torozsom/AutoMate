using FluentAssertions;
using Infrastructure.Azure;
using Infrastructure.Tests.TestSupport;

namespace Infrastructure.Tests.Azure;

/// <summary>Verifies Azure Monitor names and conversion to the shared metric units.</summary>
public sealed class AzureContainerAppClientTests
{
    /// <summary>Azure CPU nanocores become cores; working set bytes stay numeric bytes.</summary>
    [Fact]
    public async Task Metrics_use_supported_names_and_normalize_nanocores()
    {
        var handler = new DelegateHttpMessageHandler(request =>
        {
            request.RequestUri!.Query.Should().Contain("metricnames=UsageNanoCores,WorkingSetBytes");
            return DelegateHttpMessageHandler.Json("""
                                                   { "value": [
                                                     { "name": { "value": "UsageNanoCores" }, "timeseries": [{ "data": [
                                                       { "average": 1000000000 }, { "average": 2500000000 }, {} ] }] },
                                                     { "name": { "value": "WorkingSetBytes" }, "timeseries": [{ "data": [
                                                       { "average": 1572864 } ] }] }
                                                   ] }
                                                   """);
        });
        var client = new AzureContainerAppClient(new StubHttpClientFactory(handler));

        var result = await client.GetMetricsAsync("/subscriptions/sub/providers/Microsoft.App/containerApps/app",
            "test-token", CancellationToken.None);

        result!.CpuCores.Should().Be(2.5);
        result.MemoryBytes.Should().Be(1572864);
        result.Cpu.Should().Be("2.5 cores");
        result.Memory.Should().Be("1.5 MiB");
    }

    /// <summary>Missing Azure samples remain missing rather than becoming zeros.</summary>
    [Fact]
    public async Task Metrics_without_samples_preserve_gaps()
    {
        var handler = new DelegateHttpMessageHandler(_ => DelegateHttpMessageHandler.Json("""
            { "value": [{ "name": { "value": "UsageNanoCores" }, "timeseries": [{ "data": [{}] }] }] }
            """));
        var client = new AzureContainerAppClient(new StubHttpClientFactory(handler));

        var result = await client.GetMetricsAsync("/subscriptions/sub/providers/Microsoft.App/containerApps/app",
            "test-token", CancellationToken.None);

        result!.CpuCores.Should().BeNull();
        result.MemoryBytes.Should().BeNull();
        result.Cpu.Should().Be("n/a");
    }
}