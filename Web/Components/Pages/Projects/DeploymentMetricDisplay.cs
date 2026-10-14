using System.Globalization;
using Application.Abstractions.Diagnostics.Metrics;

namespace Web.Components.Pages.Projects;

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

    /// <summary>
    ///     Converts metric text into a CSS width value for the lightweight utilization bar.
    /// </summary>
    /// <param name="metricValue">Formatted observed metric text.</param>
    /// <param name="isMemoryMetric">Whether the value contains used and limit memory quantities.</param>
    /// <returns>A bounded CSS width derived from a parsed metric, or a fixed unavailable-value width.</returns>
    public static string TrackStyle(string metricValue, bool isMemoryMetric)
    {
        var percent = isMemoryMetric
            ? TryCalculateMemoryUsagePercent(metricValue)
            : TryParsePercentage(metricValue);

        if (percent is null)
            return "width: 0%;";

        var normalized = Math.Clamp(percent.Value, 0, 100);
        if (normalized > 0 && normalized < 1)
            normalized = 1;

        return $"width: {normalized.ToString("0.##", CultureInfo.InvariantCulture)}%;";
    }


    /// <summary>
    ///     Parses invariant numeric text before an optional percent sign.
    /// </summary>
    /// <param name="value">Input text to validate, normalize or format.</param>
    /// <returns>The parsed percentage, or null when the text cannot be parsed.</returns>
    private static decimal? TryParsePercentage(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var percentIndex = value.IndexOf('%', StringComparison.Ordinal);
        var numberPart = percentIndex >= 0 ? value[..percentIndex] : value;

        return decimal.TryParse(numberPart.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }


    /// <summary>
    ///     Calculates utilization only when both used and positive limit values can be parsed.
    /// </summary>
    /// <param name="memoryUsage">Observed used/limit memory text.</param>
    /// <returns>Used memory as a percentage of the positive limit, or null when either quantity is unavailable.</returns>
    private static decimal? TryCalculateMemoryUsagePercent(string memoryUsage)
    {
        var parts = memoryUsage.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return null;

        var usedBytes = TryParseDataSize(parts[0]);
        var limitBytes = TryParseDataSize(parts[1]);

        if (usedBytes is null || limitBytes is null || limitBytes <= 0)
            return null;

        return usedBytes.Value / limitBytes.Value * 100;
    }


    /// <summary>
    ///     Parses supported byte quantities with decimal or binary unit multipliers.
    /// </summary>
    /// <param name="value">Input text to validate, normalize or format.</param>
    /// <returns>The byte quantity, or null when its number or unit is unsupported.</returns>
    private static decimal? TryParseDataSize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        var numberLength = 0;

        while (numberLength < trimmed.Length &&
               (char.IsDigit(trimmed[numberLength]) || trimmed[numberLength] is '.' or ','))
            numberLength++;

        if (numberLength == 0)
            return null;

        var numberPart = trimmed[..numberLength].Replace(',', '.');
        if (!decimal.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            return null;

        var unit = trimmed[numberLength..].Trim().ToUpperInvariant();
        var multiplier = unit switch
        {
            "B" or "" => 1m,
            "KB" => 1_000m,
            "KIB" => 1_024m,
            "MB" => 1_000_000m,
            "MIB" => 1_048_576m,
            "GB" => 1_000_000_000m,
            "GIB" => 1_073_741_824m,
            _ => 1m
        };

        return amount * multiplier;
    }
}
