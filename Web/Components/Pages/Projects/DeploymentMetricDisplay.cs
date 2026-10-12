using System.Globalization;
using Application.Abstractions.Diagnostics;

namespace Web.Components.Pages;

/// <summary>
///     Formats the most recent typed observations for the project page's existing metric cards.
/// </summary>
public static class DeploymentMetricDisplay
{
    /// <summary>
    ///     Preserves multi-core CPU percentages and byte-based memory, with missing values left unknown.
    /// </summary>
    /// <param name="points">Recorded metric observations to format; missing samples remain unavailable.</param>
    /// <param name="showCpuCores">Whether CPU is displayed in native cores instead of compatibility percentages.</param>
    /// <returns>CPU and memory display text from the latest supported samples; absent values remain unavailable.</returns>
    public static (string Cpu, string Memory) Latest(IEnumerable<DeploymentMetricPoint> points,
        bool showCpuCores = false)
    {
        var latest = points.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.MaxBy(p => p.Timestamp)!);
        var cpu = latest.TryGetValue("automate_cpu_usage_cores", out var cores)
            ? showCpuCores
                ? cores.Average.ToString("0.#####", CultureInfo.InvariantCulture) + " cores"
                : (cores.Average * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%"
            : latest.TryGetValue("automate_cpu_usage_percent", out var cpuPercent)
                ? cpuPercent.Average.ToString("0.##", CultureInfo.InvariantCulture) + "%"
                : "unknown";
        var memory = latest.TryGetValue("automate_memory_used_bytes", out var used)
            ? (used.Average / 1048576).ToString("0.##", CultureInfo.InvariantCulture) + " MiB"
            : latest.TryGetValue("automate_memory_used_percent", out var memoryPercent)
                ? memoryPercent.Average.ToString("0.##", CultureInfo.InvariantCulture) + "%"
                : "unknown";
        if (latest.TryGetValue("automate_memory_limit_bytes", out var limit))
            memory += " / " + (limit.Average / 1048576).ToString("0.##", CultureInfo.InvariantCulture) + " MiB";
        return (cpu, memory);
    }
}