using Infrastructure.Templating.Catalog;
using Xunit;

namespace Tests.Templating.Catalog;

/// <summary>
///     Verifies template rendering anchors without creating directories.
/// </summary>
public sealed class TemplateRootTests
{
    /// <summary>
    ///     An .automate output directory anchors project-relative template paths at its parent.
    /// </summary>
    [Fact]
    public void ResolveTemplateRoot_AutomateDirectory_ReturnsRepositoryParent()
    {
        var repository = Path.Combine(Path.GetTempPath(), "template-unit-repository");

        var actual = TemplatePaths.ResolveTemplateRoot(Path.Combine(repository, ".automate"));

        Assert.Equal(repository, actual);
    }

    /// <summary>
    ///     An ordinary output directory remains the template anchor rather than using its parent.
    /// </summary>
    [Fact]
    public void ResolveTemplateRoot_OrdinaryDirectory_PreservesSelectedDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "template-unit-repository", "artifacts");

        var actual = TemplatePaths.ResolveTemplateRoot(directory);

        Assert.Equal(directory, actual);
    }
}
