using Infrastructure.Templating.Models;
using Xunit;

namespace Tests.Templating.Models;

/// <summary>
///     Verifies Container App secret normalization, fallbacks and length bounds.
/// </summary>
public sealed class ContainerAppSecretNameTests
{
    /// <summary>
    ///     Secret identities use lowercase names with collapsed, trimmed separators.
    /// </summary>
    [Fact]
    public void NormalizeContainerAppSecretName_InvalidCharacters_ReturnsNormalizedName()
    {
        var actual = TemplateNameNormalizer.NormalizeContainerAppSecretName("  --DB__Password!!  ");

        Assert.Equal("db-password", actual);
    }

    /// <summary>
    ///     Secret names without usable characters receive the stable secret fallback.
    /// </summary>
    /// <param name="input">A name that normalizes to an empty secret identifier.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--!!__")]
    public void NormalizeContainerAppSecretName_NoUsableCharacters_ReturnsFallback(string input)
    {
        var actual = TemplateNameNormalizer.NormalizeContainerAppSecretName(input);

        Assert.Equal("automate-secret", actual);
    }

    /// <summary>
    ///     Secret names retain the 63-character boundary and omit a separator at the truncation boundary.
    /// </summary>
    /// <param name="prefixLength">The number of letters before the separator in the input.</param>
    /// <param name="expectedLength">The retained length after applying the secret-name limit.</param>
    [Theory]
    [InlineData(62, 62)]
    [InlineData(63, 63)]
    [InlineData(64, 63)]
    public void NormalizeContainerAppSecretName_NameExceedsLimit_ReturnsBoundedNameWithoutTrailingHyphen(
        int prefixLength, int expectedLength)
    {
        var input = new string('a', prefixLength) + "-suffix";

        var actual = TemplateNameNormalizer.NormalizeContainerAppSecretName(input);

        Assert.Equal(new string('a', expectedLength), actual);
    }
}
