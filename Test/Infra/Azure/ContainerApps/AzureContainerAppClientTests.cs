using FluentAssertions;
using Infrastructure.Azure.ContainerApps;
using Test.Infra.TestSupport;

namespace Test.Infra.Azure.ContainerApps;

/// <summary>
///     Verifies Azure Monitor names and conversion to the shared metric units.
/// </summary>
public sealed class AzureContainerAppClientTests
{
    /// <summary>
    ///     Legacy custom records use only the workspace returned by the verified owning environment.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Environment_scope_resolves_workspace_without_exposing_shared_keys()
    {
        const string environment = "/subscriptions/sub/providers/Microsoft.App/managedEnvironments/env";
        var client = new AzureContainerAppClient(new StubHttpClientFactory(new DelegateHttpMessageHandler(request =>
        {
            Assert.Contains(environment, request.RequestUri!.AbsolutePath);
            return DelegateHttpMessageHandler.Json("""
                                                   {"properties":{"defaultDomain":"verified-env.region.azurecontainerapps.io","appLogsConfiguration":{"logAnalyticsConfiguration":{"customerId":"11111111-1111-1111-1111-111111111111","sharedKey":"private-key"}}}}
                                                   """);
        })));
        var scope = await client.GetEnvironmentLogScopeAsync(environment, "token", default);
        Assert.Equal("/workspaces/11111111-1111-1111-1111-111111111111", scope!.ResourceId);
        Assert.Equal("verified-env", scope.EnvironmentName);
        Assert.Equal("env", scope.ResourceName);
    }

    /// <summary>
    ///     Azure CPU nanocores become cores; working set bytes stay numeric bytes.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
    [Fact]
    public async Task Metrics_use_supported_names_and_normalize_nanocores()
    {
        var handler = new DelegateHttpMessageHandler(request =>
        {
            request.RequestUri!.Query.Should().Contain("metricnames=UsageNanoCores,WorkingSetBytes");
            request.RequestUri.Query.Should().NotContain("%2B").And.Contain("Z%2F");
            var timespan = Uri.UnescapeDataString(request.RequestUri.Query.Split('&')
                .Single(value => value.StartsWith("timespan=", StringComparison.Ordinal))["timespan=".Length..]);
            Assert.All(timespan.Split('/'),
                value => Assert.Equal(0, DateTimeOffset.Parse(value).Ticks % TimeSpan.TicksPerMinute));
            return DelegateHttpMessageHandler.Json("""
                                                   { "value": [
                                                     { "name": { "value": "UsageNanoCores" }, "timeseries": [{ "data": [
                                                       { "timeStamp": "2026-10-10T10:00:00Z", "average": 1000000000 }, { "timeStamp": "2026-10-10T10:01:00Z", "average": 2500000000 }, {} ] }] },
                                                     { "name": { "value": "WorkingSetBytes" }, "timeseries": [{ "data": [
                                                       { "timeStamp": "2026-10-10T10:01:00Z", "average": 1572864 } ] }] }
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

    /// <summary>
    ///     Missing Azure samples remain missing rather than becoming zeros.
    /// </summary>
    /// <returns>A task completing when the scenario and its assertions finish.</returns>
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
