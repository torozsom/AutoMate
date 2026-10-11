using System.Text.Json;
using Application.Abstractions.Diagnostics;

namespace Infrastructure.Azure;

/// <summary>A provider minute bucket with native timestamp and explicitly aggregated dimensions.</summary>
internal sealed record AzureMetricObservation(DateTimeOffset Timestamp, IReadOnlyList<DeploymentMetricSample> Samples);

/// <summary>Combines replicas by sum and percentage/capacity gauges by maximum without zero-filling gaps.</summary>
internal static class AzureMetricObservations
{
    /// <summary>Uses completed UTC minute boundaries so overlapping polls identify the same provider buckets.</summary>
    internal static DateTimeOffset CompletedMinute()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
    }

    /// <summary>Parses every returned minute bucket, preserving provider timestamps and finite nonnegative values.</summary>
    internal static IReadOnlyList<AzureMetricObservation> Parse(JsonElement root, string aggregation, bool sumReplicas)
    {
        var buckets = new Dictionary<DateTimeOffset, List<DeploymentMetricSample>>();
        if (!root.TryGetProperty("value", out var metrics)) return [];
        foreach (var metric in metrics.EnumerateArray())
        {
            var mapping = metric.GetProperty("name").GetProperty("value").GetString() switch
            {
                "UsageNanoCores" => ("automate_cpu_usage_cores", "cores", 1e-9),
                "WorkingSetBytes" or "usedmemory" => ("automate_memory_used_bytes", "bytes", 1d),
                "cpu_percent" or "percentProcessorTime" => ("automate_cpu_usage_percent", "percent", 1d),
                "memory_percent" => ("automate_memory_used_percent", "percent", 1d),
                "NormalizedRUConsumption" => ("automate_database_ru_usage_percent", "percent", 1d),
                _ => default
            };
            if (mapping.Item1 is null || !metric.TryGetProperty("timeseries", out var series)) continue;
            var values = new List<(DateTimeOffset Time, double Value)>();
            foreach (var dimension in series.EnumerateArray())
            {
                if (!dimension.TryGetProperty("data", out var data)) continue;
                foreach (var point in data.EnumerateArray())
                    if (point.TryGetProperty("timeStamp", out var timestamp) &&
                        DateTimeOffset.TryParse(timestamp.GetString(), out var time) &&
                        point.TryGetProperty(aggregation.ToLowerInvariant(), out var number) &&
                        number.TryGetDouble(out var value) && double.IsFinite(value) && value >= 0)
                        values.Add((time.ToUniversalTime(), value));
            }

            foreach (var minute in values.GroupBy(v => v.Time))
            {
                var value = (sumReplicas ? minute.Sum(v => v.Value) : minute.Max(v => v.Value)) * mapping.Item3;
                if (!double.IsFinite(value)) continue;
                if (!buckets.TryGetValue(minute.Key, out var samples)) buckets[minute.Key] = samples = [];
                samples.Add(new DeploymentMetricSample(mapping.Item1, value, mapping.Item2));
            }
        }

        return buckets.OrderBy(p => p.Key).Select(p => new AzureMetricObservation(p.Key, p.Value)).ToArray();
    }
}