using Infrastructure.Docker.Compose;
using Xunit;

namespace Tests.Docker.Compose;

/// <summary>
///     Verifies project-name extraction from in-memory Docker Compose JSON output.
/// </summary>
public sealed class DockerComposeProjectParserTests
{
    private static readonly string[] Expected = ["zeta", "alpha", "zeta"];

    /// <summary>
    ///     Project names retain provider order rather than being sorted or deduplicated.
    /// </summary>
    [Fact]
    public void ParseRunningProjectNames_MultipleProjects_ReturnsNamesInSourceOrder()
    {
        const string output = """
                              [{"Name":"zeta","Status":"running(1)"},{"Name":"alpha"},{"Name":"zeta"}]
                              """;

        var actual = DockerComposeProjectParser.ParseRunningProjectNames(output);

        Assert.Equal(Expected, actual);
    }

    /// <summary>
    ///     Missing output and an empty project list both represent no discovered projects.
    /// </summary>
    /// <param name="output">Synthetic Docker command output.</param>
    [Theory]
    [InlineData("")]
    [InlineData(" \r\n ")]
    [InlineData("[]")]
    public void ParseRunningProjectNames_NoProjects_ReturnsEmptyCollection(string output)
    {
        var actual = DockerComposeProjectParser.ParseRunningProjectNames(output);

        Assert.Empty(actual);
    }

    /// <summary>
    ///     Entries without a Name property do not prevent named projects from being returned.
    /// </summary>
    [Fact]
    public void ParseRunningProjectNames_EntriesWithoutNames_ReturnsOnlyNamedProjects()
    {
        const string output = """
                              [{"Status":"running(1)"},{"Name":"api"},{}]
                              """;

        var actual = DockerComposeProjectParser.ParseRunningProjectNames(output);

        Assert.Equal("api", Assert.Single(actual));
    }
}
