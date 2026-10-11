using System.Text.Json;
using Infrastructure.Azure;

namespace Infrastructure.Tests.Azure;

/// <summary>Verifies native timestamps, dimension aggregation, gaps and Cosmos units.</summary>
public sealed class AzureMetricObservationsTests
{
    /// <summary>Replicas sum at the same minute, never across time or missing observations.</summary>
    [Fact]
    public void Web_replicas_sum_per_provider_minute()
    {
        using var json = JsonDocument.Parse("""
                                            {"value":[{"name":{"value":"UsageNanoCores"},"timeseries":[
                                              {"data":[{"timeStamp":"2026-10-10T10:00:00Z","average":1000000000},{"timeStamp":"2026-10-10T10:01:00Z"}]},
                                              {"data":[{"timeStamp":"2026-10-10T10:00:00Z","average":500000000}]}]}]}
                                            """);
        var sample = Assert.Single(AzureMetricObservations.Parse(json.RootElement, "Average", true));
        Assert.Equal(DateTimeOffset.Parse("2026-10-10T10:00:00Z"), sample.Timestamp);
        Assert.Equal(1.5, Assert.Single(sample.Samples).Value);
    }

    /// <summary>Cosmos partition utilization takes the maximum and never becomes a CPU observation.</summary>
    [Fact]
    public void Cosmos_uses_maximum_native_ru_percentage()
    {
        using var json = JsonDocument.Parse("""
                                            {"value":[{"name":{"value":"NormalizedRUConsumption"},"timeseries":[
                                              {"data":[{"timeStamp":"2026-10-10T10:00:00Z","maximum":25}]},
                                              {"data":[{"timeStamp":"2026-10-10T10:00:00Z","maximum":70}]}]}]}
                                            """);
        var sample = Assert.Single(Assert.Single(AzureMetricObservations.Parse(json.RootElement, "Maximum", false))
            .Samples);
        Assert.Equal("automate_database_ru_usage_percent", sample.Name);
        Assert.Equal("percent", sample.Unit);
        Assert.Equal(70, sample.Value);
    }
}