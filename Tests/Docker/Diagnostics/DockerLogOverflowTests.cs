using System.Text;
using Infrastructure.Docker.Diagnostics;
using Xunit;

namespace Tests.Docker.Diagnostics;

/// <summary>
///     Verifies bounded line decoding and recovery after oversized input.
/// </summary>
public sealed class DockerLogOverflowTests
{
    /// <summary>
    ///     A final line exactly at the retained-character limit is not treated as overflow.
    /// </summary>
    [Fact]
    public void Feed_LineAtLimit_PreservesTextWithoutOmission()
    {
        var decoder = new DockerLogDecoder(false, TimeProvider.System);
        var input = new string('a', 8192);

        var actual = Assert.Single(decoder.Feed(Encoding.UTF8.GetBytes(input), true));

        Assert.Equal(input, actual.Text);
        Assert.False(actual.Omitted);
    }

    /// <summary>
    ///     Oversized lines remain buffered until their delimiter, then produce one omission before the next safe line.
    /// </summary>
    /// <param name="lineLength">Raw oversized line length, excluding the delimiter.</param>
    /// <param name="secretBearing">Whether the discarded line begins with a synthetic credential assignment.</param>
    /// <param name="delimiter">The delimiter arriving in a later frame.</param>
    [Theory]
    [InlineData(8193, false, "\n")]
    [InlineData(9009, true, "\r\n")]
    public void Feed_OversizedLineThenValidLine_EmitsOmissionAndRecovers(
        int lineLength, bool secretBearing, string delimiter)
    {
        var decoder = new DockerLogDecoder(false, TimeProvider.System);
        var prefix = secretBearing ? "password=" : string.Empty;
        var input = prefix + new string('s', lineLength - prefix.Length);

        var pending = decoder.Feed(Encoding.UTF8.GetBytes(input));
        var actual = decoder.Feed(Encoding.UTF8.GetBytes(delimiter + "next\n"));

        Assert.Empty(pending);
        Assert.Equal<string>(
        [
            "[Docker output line omitted because it exceeded the safe line limit.]\r\n",
            "next\n"
        ], actual.Select(line => line.Text));
        Assert.Equal([true, false], actual.Select(line => line.Omitted));
        Assert.DoesNotContain("password", actual[0].Text);
    }
}
