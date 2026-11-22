using Infrastructure.Templating.Models;
using Xunit;

namespace Tests.Templating.Models;

/// <summary>
///     Verifies resource-segment preservation and truncation boundaries.
/// </summary>
public sealed class ResourceSegmentTests
{
    /// <summary>
    ///     Normalized segments at or below the requested limit are retained in full.
    /// </summary>
    /// <param name="maximumLength">A limit equal to or larger than the normalized name length.</param>
    [Theory]
    [InlineData(6)]
    [InlineData(20)]
    public void NormalizeResourceSegment_NameWithinLimit_ReturnsCompleteNormalizedName(int maximumLength)
    {
        var actual = TemplateNameNormalizer.NormalizeResourceSegment(" MY APP ", maximumLength);

        Assert.Equal("my-app", actual);
    }

    /// <summary>
    ///     Oversized segments are bounded and do not end with a separator created by truncation.
    /// </summary>
    /// <param name="maximumLength">The resource-segment length limit.</param>
    /// <param name="expected">The bounded segment after removing any trailing hyphen.</param>
    [Theory]
    [InlineData(6, "alpha")]
    [InlineData(8, "alpha-be")]
    public void NormalizeResourceSegment_NameExceedsLimit_ReturnsTrimmedPrefix(int maximumLength, string expected)
    {
        var actual = TemplateNameNormalizer.NormalizeResourceSegment("alpha-beta", maximumLength);

        Assert.Equal(expected, actual);
    }
}
