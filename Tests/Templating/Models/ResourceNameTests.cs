using Infrastructure.Templating.Models;
using Xunit;

namespace Tests.Templating.Models;

/// <summary>
///     Verifies resource-name normalization and fallback identities.
/// </summary>
public sealed class ResourceNameTests
{
    /// <summary>
    ///     Resource identities use lowercase segments separated by single hyphens.
    /// </summary>
    [Fact]
    public void NormalizeResourceName_MixedCaseAndInvalidCharacters_ReturnsNormalizedName()
    {
        var actual = TemplateNameNormalizer.NormalizeResourceName("  --My APP__API!!  ");

        Assert.Equal("my-app-api", actual);
    }

    /// <summary>
    ///     Unusable resource names receive the stable application fallback.
    /// </summary>
    /// <param name="input">A name without usable resource-name characters.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--!!__")]
    public void NormalizeResourceName_NoUsableCharacters_ReturnsFallback(string input)
    {
        var actual = TemplateNameNormalizer.NormalizeResourceName(input);

        Assert.Equal("automate-app", actual);
    }
}
