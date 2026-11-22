using Infrastructure.Docker.Diagnostics;
using Xunit;

namespace Tests.Docker.Diagnostics;

/// <summary>
///     Verifies provider timestamp extraction without relying on wall-clock assertions.
/// </summary>
public sealed class DockerLogTimestampTests
{
    /// <summary>
    ///     A valid provider timestamp is retained as metadata and converted to UTC.
    /// </summary>
    [Fact]
    public void Feed_ValidProviderTimestamp_SeparatesTimestampFromMessage()
    {
        var decoder = new DockerLogDecoder(true, TimeProvider.System);

        var actual = Assert.Single(decoder.Feed("2026-01-02T03:04:05+02:00 ready\n"u8));

        Assert.Equal("ready\n", actual.Text);
        Assert.Equal("2026-01-02T03:04:05+02:00", actual.ProviderTimestamp);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 1, 4, 5, TimeSpan.Zero), actual.Timestamp);
    }

    /// <summary>
    ///     An unrecognized prefix remains message text rather than being stripped as provider metadata.
    /// </summary>
    [Fact]
    public void Feed_InvalidTimestampPrefix_PreservesOriginalMessage()
    {
        var decoder = new DockerLogDecoder(true, TimeProvider.System);

        var actual = Assert.Single(decoder.Feed("not-a-date ready\n"u8));

        Assert.Equal("not-a-date ready\n", actual.Text);
        Assert.Null(actual.ProviderTimestamp);
    }
}
