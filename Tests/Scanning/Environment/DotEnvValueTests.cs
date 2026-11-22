using Infrastructure.Scanner.Environment;
using Xunit;

namespace Tests.Scanning.Environment;

/// <summary>
///     Verifies that assignment values retain embedded equals signs.
/// </summary>
public sealed class DotEnvValueTests
{
    /// <summary>
    ///     Only the first equals sign separates the key from the complete value.
    /// </summary>
    [Fact]
    public void TryParse_ValueContainsEqualsSigns_PreservesCompleteValue()
    {
        var success = DotEnvLineParser.TryParse("APP_OPTIONS=mode=fast;retries=3", out var actual);

        Assert.True(success);
        Assert.Equal("APP_OPTIONS", actual.Key);
        Assert.Equal("mode=fast;retries=3", actual.Value);
    }
}
