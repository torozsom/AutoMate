using Infrastructure.Docker.Diagnostics;
using Xunit;

namespace Tests.Docker.Diagnostics;

/// <summary>
///     Verifies display-value parsing from in-memory Docker stats lines.
/// </summary>
public sealed class DockerMetricsLineTests
{
    /// <summary>
    ///     ANSI sequences and surrounding whitespace do not become part of CPU or memory display values.
    /// </summary>
    [Fact]
    public void TryParse_AnsiDecoratedValues_ReturnsTrimmedCpuAndMemory()
    {
        var success = DockerMetricsLine.TryParse("\u001b[32m 12.5% \u001b[0m| 64MiB / 1GiB ", out var actual);

        Assert.True(success);
        Assert.Equal("12.5%", actual.Cpu);
        Assert.Equal("64MiB / 1GiB", actual.Memory);
    }

    /// <summary>
    ///     A stats line without the required delimiter does not produce a partial observation.
    /// </summary>
    [Fact]
    public void TryParse_MissingDelimiter_ReturnsFalseWithoutPartialMetrics()
    {
        var success = DockerMetricsLine.TryParse("12.5%", out var actual);

        Assert.False(success);
        Assert.Equal(default, actual);
    }
}
