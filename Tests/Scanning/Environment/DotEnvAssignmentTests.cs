using Infrastructure.Scanner.Environment;
using Xunit;

namespace Tests.Scanning.Environment;

/// <summary>
///     Verifies assignment whitespace and explicitly empty values.
/// </summary>
public sealed class DotEnvAssignmentTests
{
    /// <summary>
    ///     Assignment parsing trims the line, key and value independently.
    /// </summary>
    [Fact]
    public void TryParse_AssignmentWithWhitespace_ReturnsTrimmedKeyAndValue()
    {
        var success = DotEnvLineParser.TryParse("  APP_PORT = 8080  ", out var actual);

        Assert.True(success);
        Assert.Equal("APP_PORT", actual.Key);
        Assert.Equal("8080", actual.Value);
    }

    /// <summary>
    ///     An explicit empty value remains a valid assignment rather than a missing variable.
    /// </summary>
    /// <param name="line">An assignment whose value is empty, optionally quoted.</param>
    [Theory]
    [InlineData("APP_NAME=")]
    [InlineData("APP_NAME=   ")]
    [InlineData("APP_NAME=''")]
    [InlineData("APP_NAME=\"\"")]
    public void TryParse_EmptyValue_ReturnsSuccessfulEmptyAssignment(string line)
    {
        var success = DotEnvLineParser.TryParse(line, out var actual);

        Assert.True(success);
        Assert.Equal("APP_NAME", actual.Key);
        Assert.Equal(string.Empty, actual.Value);
    }
}
