using Infrastructure.Templating.Catalog;
using Xunit;

namespace Tests.Templating.Catalog;

/// <summary>
///     Verifies generated-output containment using lexical path resolution only.
/// </summary>
public sealed class OutputPathContainmentTests
{
    /// <summary>
    ///     A nested artifact remains beneath its selected output directory.
    /// </summary>
    [Fact]
    public void ResolveOutputPath_NestedArtifact_ReturnsPathUnderSelectedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "template-unit-root");

        var actual = TemplatePaths.ResolveOutputPath(root, Path.Combine("deploy", "compose.yaml"));

        Assert.Equal(Path.Combine(root, "deploy", "compose.yaml"), actual);
    }

    /// <summary>
    ///     A sibling directory cannot pass containment merely by sharing the root-name prefix.
    /// </summary>
    [Fact]
    public void ResolveOutputPath_SiblingWithSharedRootPrefix_ThrowsInvalidOperationException()
    {
        var root = Path.Combine(Path.GetTempPath(), "template-unit-root");
        var relativePath = Path.Combine("..", "template-unit-root-other", "compose.yaml");

        Assert.Throws<InvalidOperationException>(() => TemplatePaths.ResolveOutputPath(root, relativePath));
    }
}
