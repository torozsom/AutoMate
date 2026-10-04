using Application.Abstractions.Diagnostics;
using Web.Components.Shared;
using Xunit;

namespace Web.Tests;

/// <summary>Resource display must retain small values and actual sample weighting.</summary>
public sealed class TelemetryPresentationTests
{
    /// <summary>Memory is expressed in binary units and sparse CPU values remain legible.</summary>
    [Fact]
    public void Formats_resource_units_without_exponents()
    {
        Assert.EndsWith(" MiB", TelemetryPresentation.Format(19430000, TelemetryPresentation.Memory));
        Assert.DoesNotContain("E+", TelemetryPresentation.Format(32490000000, TelemetryPresentation.MemoryLimit));
        Assert.Contains("006", TelemetryPresentation.Format(.006, TelemetryPresentation.Cpu));
        Assert.Equal("0 cores", TelemetryPresentation.Format(0, TelemetryPresentation.Cpu));
        Assert.Equal("—", TelemetryPresentation.Format(null, TelemetryPresentation.Memory));
        Assert.Equal("—", TelemetryPresentation.Format(double.NaN, TelemetryPresentation.Cpu));
    }

    /// <summary>Daily resource averages weight unequal sample counts and preserve absent days.</summary>
    [Fact]
    public void Daily_points_weight_observations_without_filling_gaps()
    {
        var date = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        DeploymentAnalyticsRow Row(DateTimeOffset day, long count, double? value) =>
            new(Guid.NewGuid(), day, "web", TelemetryPresentation.Cpu, "cores", count, value, value, value, 0, true, day);
        var rows = new[] { Row(date, 1, .01), Row(date, 9, .03), Row(date.AddDays(2), 1, 0), Row(date.AddDays(1), 0, null) };
        var points = TelemetryPresentation.DailyPoints(rows);
        Assert.Equal(2, points.Count);
        Assert.Equal(.028, points[0].Average, 8);
        Assert.Equal(date.AddDays(2), points[1].Timestamp);
        Assert.Equal(0, points[1].Average);
        Assert.Null(TelemetryPresentation.WeightedAverage(rows.Where(r => r.SampleCount == 0)));
    }

    /// <summary>Zero, constant and isolated observations receive valid axes without forcing one core.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(.006)]
    [InlineData(.012)]
    public void Sparse_cpu_axes_are_nonzero_and_preserve_scale(double value)
    {
        var maximum = TelemetryPresentation.AxisMaximum([new(DateTimeOffset.UtcNow, value, value, value)]);
        Assert.True(maximum > 0);
        Assert.True(maximum >= value);
        Assert.True(maximum < 1);
    }
}
