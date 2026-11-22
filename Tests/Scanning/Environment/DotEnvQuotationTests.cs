using Infrastructure.Scanner.Environment;
using Xunit;

namespace Tests.Scanning.Environment;

/// <summary>
///     Verifies matching outer quotes and preservation of quoted whitespace.
/// </summary>
public sealed class DotEnvQuotationTests
{
    /// <summary>
    ///     Matching outer quotes are removed while whitespace inside the quoted value is preserved.
    /// </summary>
    /// <param name="line">An assignment with matching single or double quotes.</param>
    [Theory]
    [InlineData("APP_NAME=' My App '")]
    [InlineData("APP_NAME=\" My App \"")]
    public void TryParse_MatchingOuterQuotes_ReturnsUnquotedValue(string line)
    {
        var success = DotEnvLineParser.TryParse(line, out var actual);

        Assert.True(success);
        Assert.Equal("APP_NAME", actual.Key);
        Assert.Equal(" My App ", actual.Value);
    }
}
