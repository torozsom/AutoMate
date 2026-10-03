using System.Globalization;
using System.Text.RegularExpressions;
using Application.Abstractions.Diagnostics;

namespace Infrastructure.Diagnostics;

/// <summary>Converts provider display values to explicit numeric units without filling missing values.</summary>
internal static partial class DeploymentMetricNormalizer
{
    /// <summary>Docker percent represents CPU core usage times one hundred and can exceed 100.</summary>
    internal static IReadOnlyList<DeploymentMetricSample> Docker(string cpu, string memory)
    {
        var result = new List<DeploymentMetricSample>();
        if (double.TryParse(cpu.Trim().TrimEnd('%'), CultureInfo.InvariantCulture, out var percent) &&
            double.IsFinite(percent) && percent >= 0)
            result.Add(new DeploymentMetricSample("automate_cpu_usage_cores", percent / 100, "cores"));
        var pieces = memory.Split('/');
        if (Bytes(pieces[0]) is { } used)
            result.Add(new DeploymentMetricSample("automate_memory_used_bytes", used, "bytes"));
        if (pieces.Length > 1 && Bytes(pieces[1]) is { } limit)
            result.Add(new DeploymentMetricSample("automate_memory_limit_bytes", limit, "bytes"));
        return result;
    }

    /// <summary>Parses Docker decimal/binary byte units, preserving their distinction.</summary>
    private static double? Bytes(string value)
    {
        var match = BytePattern().Match(value.Trim());
        if (!match.Success ||
            !double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var amount)) return null;
        var unit = match.Groups[2].Value;
        var factor = unit switch
        {
            "B" => 1d,
            "kB" or "KB" => 1000d,
            "MB" => 1e6,
            "GB" => 1e9,
            "TB" => 1e12,
            "KiB" => 1024d,
            "MiB" => 1048576d,
            "GiB" => 1073741824d,
            "TiB" => 1099511627776d,
            _ => double.NaN
        };
        var bytes = amount * factor;
        return double.IsFinite(bytes) && bytes >= 0 ? bytes : null;
    }

    /// <summary>Accepts only explicit, known byte suffixes.</summary>
    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*([A-Za-z]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex BytePattern();
}