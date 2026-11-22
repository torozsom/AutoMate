using Infrastructure.Templating.Catalog;
using Xunit;

namespace Tests.Templating.Catalog;

/// <summary>
///     Verifies manifest-relative output paths without accessing the filesystem.
/// </summary>
public sealed class RelativeOutputPathTests
{
    /// <summary>
    ///     Manifest paths use forward slashes independently of the input separator style.
    /// </summary>
    [Fact]
    public void NormalizeRelativeOutputPath_BackslashSeparators_ReturnsForwardSlashes()
    {
        var actual = TemplatePaths.NormalizeRelativeOutputPath(@".automate\deploy\compose.yaml");

        Assert.Equal(".automate/deploy/compose.yaml", actual);
    }

    /// <summary>
    ///     Parent-directory segments are rejected before a manifest output can be resolved.
    /// </summary>
    [Fact]
    public void NormalizeRelativeOutputPath_ParentTraversal_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TemplatePaths.NormalizeRelativeOutputPath("deploy/../outside.yaml"));
    }
}
