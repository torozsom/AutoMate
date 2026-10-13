using System.Globalization;
using Application.Abstractions.Diagnostics;

namespace Web.Components.Shared;

/// <summary>
///     Presentation-only names, units and chart aggregates; never synthesizes missing measurements.
/// </summary>
public static class TelemetryPresentation
{
    /// <summary>CPU gauge name used by existing history contracts.</summary>
    public const string Cpu = "automate_cpu_usage_cores";

    /// <summary>Observed memory usage gauge.</summary>
    public const string Memory = "automate_memory_used_bytes";

    /// <summary>Reported container memory capacity gauge.</summary>
    public const string MemoryLimit = "automate_memory_limit_bytes";

    /// <summary>
    ///     Maps storage names to readable labels without hiding unknown measurements.
    /// </summary>
    /// <param name="name">Metric or configuration name to resolve.</param>
    /// <returns>The known metric label, or the original unknown name.</returns>
    public static string Label(string name)
    {
        return name switch
        {
            Cpu => "CPU usage",
            Memory => "Memory usage",
            MemoryLimit => "Memory limit",
            "automate_cpu_usage_percent" => "CPU usage (%)",
            "automate_memory_used_percent" => "Memory usage (%)",
            "automate_database_ru_usage_percent" => "Database RU usage (%)",
            "observed_log_errors" => "Observed log errors",
            _ => name
        };
    }

    /// <summary>
    ///     Formats finite values without exponent notation; absent values remain unavailable.
    /// </summary>
    /// <param name="value">Observed finite value, or null when unavailable.</param>
    /// <param name="metric">Stored metric series name.</param>
    /// <returns>A readable metric value with its unit, or an unavailable marker for absent/nonfinite values.</returns>
    public static string Format(double? value, string metric)
    {
        if (value is not { } number || !double.IsFinite(number)) return "—";
        if (metric is Memory or MemoryLimit)
        {
            string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
            var unit = 0;
            while (Math.Abs(number) >= 1024 && unit < units.Length - 1)
            {
                number /= 1024;
                unit++;
            }

            return number.ToString("0.##", CultureInfo.CurrentCulture) + " " + units[unit];
        }

        return number.ToString("0.#########", CultureInfo.CurrentCulture) + (metric == Cpu ? " cores" :
            metric.EndsWith("_percent", StringComparison.Ordinal) ? "%" : "");
    }

    /// <summary>
    ///     Weights daily means by actual sample counts, excluding missing rows.
    /// </summary>
    /// <param name="rows">Recorded daily metric aggregates.</param>
    /// <returns>The sample-weighted mean, or null when no supported observations exist.</returns>
    public static double? WeightedAverage(IEnumerable<DeploymentAnalyticsRow> rows)
    {
        var observed = rows.Where(r => r.SampleCount > 0 && r.Average is { } a && double.IsFinite(a)).ToArray();
        var count = observed.Sum(r => r.SampleCount);
        return count == 0 ? null : observed.Sum(r => r.Average!.Value * r.SampleCount) / count;
    }

    /// <summary>
    ///     Builds one sample-weighted point per observed UTC day.
    /// </summary>
    /// <param name="rows">Recorded daily metric aggregates.</param>
    /// <returns>Observed chart points in timestamp order, without synthetic missing samples.</returns>
    public static IReadOnlyList<TelemetryChartPoint> DailyPoints(IEnumerable<DeploymentAnalyticsRow> rows)
    {
        return rows.Where(r => r.SampleCount > 0 && r.Average.HasValue).GroupBy(r => r.DayUtc)
            .OrderBy(g => g.Key).Select(g => new TelemetryChartPoint(g.Key, WeightedAverage(g)!.Value,
                g.Min(r => r.Minimum), g.Max(r => r.Maximum))).ToArray();
    }

    /// <summary>
    ///     Chooses a rounded nonzero axis ceiling while retaining sub-core CPU resolution.
    /// </summary>
    /// <param name="points">Recorded metric observations to format; missing samples remain unavailable.</param>
    /// <returns>A rounded positive chart ceiling that retains fractional-core resolution.</returns>
    public static double AxisMaximum(IEnumerable<TelemetryChartPoint> points)
    {
        var max = points.Select(p => Math.Max(p.Average, p.Maximum ?? p.Average)).DefaultIfEmpty(0).Max();
        if (!double.IsFinite(max) || max <= 0) return 0.001;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(max)));
        var normalized = max / magnitude;
        return (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10) * magnitude;
    }
}

/// <summary>
///     A chart observation; extrema are optional for presentation of incomplete aggregates.
/// </summary>
/// <param name="Timestamp">Recorded timestamp, if available.</param>
/// <param name="Average">Observed mean value.</param>
/// <param name="Minimum">Observed minimum, or null when unavailable.</param>
/// <param name="Maximum">Observed maximum, or null when unavailable.</param>
public sealed record TelemetryChartPoint(
    DateTimeOffset Timestamp,
    double Average,
    double? Minimum = null,
    double? Maximum = null);
