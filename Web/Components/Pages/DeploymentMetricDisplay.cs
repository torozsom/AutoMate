using System.Globalization;
using Application.Abstractions.Diagnostics;

namespace Web.Components.Pages;

/// <summary>Formats the most recent typed observations for the project page's existing metric cards.</summary>
public static class DeploymentMetricDisplay
{
    /// <summary>Preserves multi-core CPU percentages and byte-based memory, with missing values left unknown.</summary>
    public static (string Cpu, string Memory) Latest(IEnumerable<DeploymentMetricPoint> points)
    {
        var latest = points.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.MaxBy(p => p.Timestamp)!);
        var cpu = latest.TryGetValue("automate_cpu_usage_cores", out var cores)
            ? (cores.Average * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%"
            : "unknown";
        var memory = latest.TryGetValue("automate_memory_used_bytes", out var used)
            ? (used.Average / 1048576).ToString("0.##", CultureInfo.InvariantCulture) + " MiB"
            : "unknown";
        if (latest.TryGetValue("automate_memory_limit_bytes", out var limit))
            memory += " / " + (limit.Average / 1048576).ToString("0.##", CultureInfo.InvariantCulture) + " MiB";
        return (cpu, memory);
    }
}