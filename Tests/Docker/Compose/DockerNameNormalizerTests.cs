using Infrastructure.Docker.Compose;
using Xunit;

namespace Tests.Docker.Compose;

/// <summary>
///     Verifies Compose project naming rules without starting Docker processes.
/// </summary>
public sealed class DockerNameNormalizerTests
{
    /// <summary>
    ///     User-supplied casing and surrounding whitespace do not change the normalized identity.
    /// </summary>
    [Fact]
    public void NormalizeProjectName_MixedCaseWithWhitespace_ReturnsLowercaseName()
    {
        var actual = DockerNameNormalizer.NormalizeProjectName("  MyApp  ");

        Assert.Equal("myapp", actual);
    }

    /// <summary>
    ///     Invalid characters and repeated hyphens produce a single separator between name segments.
    /// </summary>
    [Fact]
    public void NormalizeProjectName_InvalidCharactersAndRepeatedHyphens_ReturnsCollapsedSeparators()
    {
        var actual = DockerNameNormalizer.NormalizeProjectName("--My App!!---API--");

        Assert.Equal("my-app-api", actual);
    }

    /// <summary>
    ///     Supported underscores, hyphens and digits retain their original positions.
    /// </summary>
    [Fact]
    public void NormalizeProjectName_SupportedCharacters_PreservesName()
    {
        var actual = DockerNameNormalizer.NormalizeProjectName("team_api-v2");

        Assert.Equal("team_api-v2", actual);
    }

    /// <summary>
    ///     Names without usable characters receive the stable project fallback.
    /// </summary>
    /// <param name="input">Input value for the documented behavior.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("---")]
    public void NormalizeProjectName_NoUsableCharacters_ReturnsFallback(string input)
    {
        var actual = DockerNameNormalizer.NormalizeProjectName(input);

        Assert.Equal("automate-project", actual);
    }
}
