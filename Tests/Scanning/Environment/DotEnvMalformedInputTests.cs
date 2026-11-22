using Infrastructure.Scanner.Environment;
using Xunit;

namespace Tests.Scanning.Environment;

/// <summary>
///     Verifies rejection of assignments without separators or keys.
/// </summary>
public sealed class DotEnvMalformedInputTests
{
    /// <summary>
    ///     Missing separators and absent keys are rejected without returning a partial assignment.
    /// </summary>
    /// <param name="line">An assignment missing its separator or key.</param>
    [Theory]
    [InlineData("APP_NAME")]
    [InlineData("=value")]
    [InlineData("   =value")]
    public void TryParse_MissingSeparatorOrKey_ReturnsFalse(string line)
    {
        var success = DotEnvLineParser.TryParse(line, out var actual);

        Assert.False(success);
        Assert.Equal(default, actual);
    }
}
