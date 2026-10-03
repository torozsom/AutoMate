using Application.Abstractions.Diagnostics;
using Web.Components.Pages;
using Xunit;

namespace Web.Tests;

/// <summary>Verifies restored metric cards preserve numeric units and missing observations.</summary>
public sealed class DeploymentMetricDisplayTests
{
    /// <summary>The newest saved values override older samples and can represent several CPU cores.</summary>
    [Fact]
    public void Formats_latest_saved_samples_without_zero_filling()
    {
        var now = DateTimeOffset.UtcNow;

        DeploymentMetricPoint Point(string name, string unit, double value, DateTimeOffset timestamp)
        {
            return new DeploymentMetricPoint("web", name, unit, timestamp, value, value, value);
        }

        var display = DeploymentMetricDisplay.Latest([
            Point("automate_cpu_usage_cores", "cores", 0.1, now.AddMinutes(-1)),
            Point("automate_cpu_usage_cores", "cores", 2.5, now),
            Point("automate_memory_used_bytes", "bytes", 104857600, now),
            Point("automate_memory_limit_bytes", "bytes", 209715200, now)
        ]);
        Assert.Equal("250%", display.Cpu);
        Assert.Equal("100 MiB / 200 MiB", display.Memory);
        Assert.Equal(("unknown", "unknown"), DeploymentMetricDisplay.Latest([]));
    }
}