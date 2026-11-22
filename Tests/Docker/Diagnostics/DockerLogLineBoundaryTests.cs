using Infrastructure.Docker.Diagnostics;
using Xunit;

namespace Tests.Docker.Diagnostics;

/// <summary>
///     Verifies incremental line assembly and preservation of line delimiters.
/// </summary>
public sealed class DockerLogLineBoundaryTests
{
    /// <summary>
    ///     A CRLF delimiter split across frames produces one line with its original delimiter.
    /// </summary>
    [Fact]
    public void Feed_CarriageReturnAndLineFeedAcrossFrames_EmitsOneCompleteLine()
    {
        var decoder = new DockerLogDecoder(false, TimeProvider.System);

        var pending = decoder.Feed("first\r"u8);
        var completed = decoder.Feed("\n"u8);

        Assert.Empty(pending);
        Assert.Equal("first\r\n", Assert.Single(completed).Text);
    }

    /// <summary>
    ///     Flushing emits an unterminated final line exactly once without adding a delimiter.
    /// </summary>
    [Fact]
    public void Feed_FinalPartialLineWithFlush_EmitsRemainingText()
    {
        var decoder = new DockerLogDecoder(false, TimeProvider.System);

        var pending = decoder.Feed("last line"u8);
        var completed = decoder.Feed([], true);
        var repeatedFlush = decoder.Feed([], true);

        Assert.Empty(pending);
        Assert.Equal("last line", Assert.Single(completed).Text);
        Assert.Empty(repeatedFlush);
    }

    /// <summary>
    ///     Independent stdout/stderr decoders keep interleaved partial lines apart and retain bare CR.
    /// </summary>
    [Fact]
    public void Separate_streams_preserve_partial_lines_and_carriage_returns()
    {
        var stdout = new DockerLogDecoder(false, TimeProvider.System);
        var stderr = new DockerLogDecoder(false, TimeProvider.System);

        Assert.Empty(stdout.Feed("out"u8));
        Assert.Empty(stderr.Feed("err"u8));
        Assert.Equal("output\r\n", Assert.Single(stdout.Feed("put\r\n"u8)).Text);
        Assert.Equal(["error\r", "next"], stderr.Feed("or\rnext"u8, true).Select(e => e.Text));
    }
}
