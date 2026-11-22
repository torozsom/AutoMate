using Infrastructure.Scanner.Environment;
using Xunit;

namespace Tests.Scanning.Environment;

/// <summary>
///     Verifies optional export prefixes without evaluating shell commands.
/// </summary>
public sealed class DotEnvExportTests
{
    /// <summary>
    ///     The optional export prefix is removed case-insensitively before assignment parsing.
    /// </summary>
    /// <param name="line">An exported environment-variable assignment.</param>
    [Theory]
    [InlineData("export APP_PORT=8080")]
    [InlineData("  EXPORT   APP_PORT=8080")]
    public void TryParse_ExportPrefix_ReturnsAssignment(string line)
    {
        var success = DotEnvLineParser.TryParse(line, out var actual);

        Assert.True(success);
        Assert.Equal("APP_PORT", actual.Key);
        Assert.Equal("8080", actual.Value);
    }
}
