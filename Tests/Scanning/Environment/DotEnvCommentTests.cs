using Infrastructure.Scanner.Environment;
using Xunit;

namespace Tests.Scanning.Environment;

/// <summary>
///     Verifies blank lines, full-line comments and supported unquoted inline comments.
/// </summary>
public sealed class DotEnvCommentTests
{
    /// <summary>
    ///     Blank and full-line comment inputs do not produce an assignment.
    /// </summary>
    /// <param name="line">An empty line, whitespace or a full-line comment.</param>
    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("# APP_PORT=8080")]
    [InlineData("  # configuration comment")]
    public void TryParse_BlankOrCommentLine_ReturnsFalse(string line)
    {
        var success = DotEnvLineParser.TryParse(line, out var actual);

        Assert.False(success);
        Assert.Equal(default, actual);
    }

    /// <summary>
    ///     Supported space-prefixed comments are removed from unquoted values.
    /// </summary>
    [Fact]
    public void TryParse_UnquotedInlineComment_ReturnsValueWithoutComment()
    {
        var success = DotEnvLineParser.TryParse("APP_PORT=8080 # local port", out var actual);

        Assert.True(success);
        Assert.Equal("APP_PORT", actual.Key);
        Assert.Equal("8080", actual.Value);
    }
}
