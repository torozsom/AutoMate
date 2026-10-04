using System.Globalization;
using Application.Abstractions.Diagnostics;

namespace Web.Components.Shared;

/// <summary>Presentation-only names, units and chart aggregates; never synthesizes missing measurements.</summary>
public static class TelemetryPresentation
{
    /// <summary>CPU gauge name used by existing history contracts.</summary>
    public const string Cpu = "automate_cpu_usage_cores";
    /// <summary>Observed memory usage gauge.</summary>
    public const string Memory = "automate_memory_used_bytes";
    /// <summary>Reported container memory capacity gauge.</summary>
    public const string MemoryLimit = "automate_memory_limit_bytes";

    /// <summary>Maps storage names to readable labels without hiding unknown measurements.</summary>
    public static string Label(string name) => name switch
    {
        Cpu => "CPU usage", Memory => "Memory usage", MemoryLimit => "Memory limit",
        "observed_log_errors" => "Observed log errors", _ => name
    };

    /// <summary>Formats finite values without exponent notation; absent values remain unavailable.</summary>
    public static string Format(double? value, string metric)
    {
        if (value is not { } number || !double.IsFinite(number)) return "—";
        if (metric is Memory or MemoryLimit)
        {
            string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
            var unit = 0;
            while (Math.Abs(number) >= 1024 && unit < units.Length - 1) { number /= 1024; unit++; }
            return number.ToString("0.##", CultureInfo.CurrentCulture) + " " + units[unit];
        }
        return number.ToString("0.#########", CultureInfo.CurrentCulture) + (metric == Cpu ? " cores" : "");
    }

    /// <summary>Weights daily means by actual sample counts, excluding missing rows.</summary>
    public static double? WeightedAverage(IEnumerable<DeploymentAnalyticsRow> rows)
    {
        var observed = rows.Where(r => r.SampleCount > 0 && r.Average is { } a && double.IsFinite(a)).ToArray();
        var count = observed.Sum(r => r.SampleCount);
        return count == 0 ? null : observed.Sum(r => r.Average!.Value * r.SampleCount) / count;
    }

    /// <summary>Builds one sample-weighted point per observed UTC day.</summary>
    public static IReadOnlyList<TelemetryChartPoint> DailyPoints(IEnumerable<DeploymentAnalyticsRow> rows) =>
        rows.Where(r => r.SampleCount > 0 && r.Average.HasValue).GroupBy(r => r.DayUtc)
            .OrderBy(g => g.Key).Select(g => new TelemetryChartPoint(g.Key, WeightedAverage(g)!.Value,
                g.Min(r => r.Minimum), g.Max(r => r.Maximum))).ToArray();

    /// <summary>Chooses a rounded nonzero axis ceiling while retaining sub-core CPU resolution.</summary>
    public static double AxisMaximum(IEnumerable<TelemetryChartPoint> points)
    {
        var max = points.Select(p => Math.Max(p.Average, p.Maximum ?? p.Average)).DefaultIfEmpty(0).Max();
        if (!double.IsFinite(max) || max <= 0) return 0.001;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(max)));
        var normalized = max / magnitude;
        return (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10) * magnitude;
    }
}

/// <summary>A chart observation; extrema are optional for presentation of incomplete aggregates.</summary>
public sealed record TelemetryChartPoint(DateTimeOffset Timestamp, double Average, double? Minimum = null, double? Maximum = null);
