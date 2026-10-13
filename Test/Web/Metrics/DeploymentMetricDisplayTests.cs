using Application.Abstractions.Diagnostics;
using Web.Components.Pages;
using Web.Components.Shared;
using Xunit;

namespace Test.Web;

/// <summary>
///     Verifies restored metric cards preserve numeric units and missing observations.
/// </summary>
public sealed class DeploymentMetricDisplayTests
{
    /// <summary>
    ///     Extracted metric tracks retain missing values, saturation and minimum visible widths.
    /// </summary>
    /// <param name="text">The existing formatted metric value.</param>
    /// <param name="memory">Whether to parse used/limit quantities.</param>
    /// <param name="expected">The legacy CSS width.</param>
    [Theory]
    [InlineData("unknown", false, "width: 0%;")]
    [InlineData("250%", false, "width: 100%;")]
    [InlineData("0.2%", false, "width: 1%;")]
    [InlineData("1 MiB / 2 MiB", true, "width: 50%;")]
    public void Track_styles_preserve_legacy_display_rules(string text, bool memory, string expected)
    {
        Assert.Equal(expected, DeploymentMetricDisplay.TrackStyle(text, memory));
    }

    /// <summary>
    ///     The newest saved values override older samples and can represent several CPU cores.
    /// </summary>
    [Fact]
    public void Formats_latest_saved_samples_without_zero_filling()
    {
        var now = DateTimeOffset.UtcNow;

#pragma warning disable CS1587 // C# does not emit local-function XML documentation.
        /// <summary>
        ///     Creates point data with the supplied identities and synthetic scenario values.
        /// </summary>
        /// <param name="name">Fixture member, option or metric name selected by the caller.</param>
        /// <param name="unit">The native metric unit associated with the sample.</param>
        /// <param name="value">Synthetic numeric observation.</param>
        /// <param name="timestamp">Recorded timestamp, if available.</param>
        /// <returns>The configured deployment metric point instance or response for this scenario.</returns>
        DeploymentMetricPoint Point(string name, string unit, double value, DateTimeOffset timestamp)
        {
            return new DeploymentMetricPoint("web", name, unit, timestamp, value, value, value);
        }
#pragma warning restore CS1587

        var display = DeploymentMetricDisplay.Latest([
            Point("automate_cpu_usage_cores", "cores", 0.1, now.AddMinutes(-1)),
            Point("automate_cpu_usage_cores", "cores", 2.5, now),
            Point("automate_memory_used_bytes", "bytes", 104857600, now),
            Point("automate_memory_limit_bytes", "bytes", 209715200, now)
        ]);
        Assert.Equal("250%", display.Cpu);
        Assert.Equal("2.5 cores", DeploymentMetricDisplay.Latest([
            Point("automate_cpu_usage_cores", "cores", 2.5, now)
        ], true).Cpu);
        Assert.Equal("100 MiB / 200 MiB", display.Memory);
        Assert.Equal(("unknown", "unknown"), DeploymentMetricDisplay.Latest([]));
    }

    /// <summary>
    ///     Azure database percentages survive reload without core conversion or zero filling.
    /// </summary>
    [Fact]
    public void Formats_native_database_percentages()
    {
        var now = DateTimeOffset.UtcNow;
        var display = DeploymentMetricDisplay.Latest([
            new DeploymentMetricPoint("db", "automate_cpu_usage_percent", "percent", now, 42, 42, 42),
            new DeploymentMetricPoint("db", "automate_memory_used_percent", "percent", now, 65, 65, 65)
        ]);
        Assert.Equal(("42%", "65%"), display);
        Assert.Equal("42%", TelemetryPresentation.Format(42, "automate_cpu_usage_percent"));
    }
}