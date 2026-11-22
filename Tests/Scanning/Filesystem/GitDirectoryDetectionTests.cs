using Infrastructure.Scanner.Filesystem;
using Xunit;

namespace Tests.Scanning.Filesystem;

/// <summary>
///     Verifies Git metadata-directory detection from supplied names without enumerating real directories.
/// </summary>
public sealed class GitDirectoryDetectionTests
{
    /// <summary>
    ///     Git metadata detection compares the final directory name case-insensitively.
    /// </summary>
    [Fact]
    public void ContainsGitDirectory_MixedCaseGitBasename_ReturnsTrue()
    {
        string[] directories = [Path.Combine("repository", "src"), Path.Combine("repository", ".GiT")];

        var actual = LocalScannerDirectoryRules.ContainsGitDirectory(directories);

        Assert.True(actual);
    }

    /// <summary>
    ///     A parent named .git or a similar basename does not identify a candidate as Git metadata.
    /// </summary>
    [Fact]
    public void ContainsGitDirectory_OnlySimilarNames_ReturnsFalse()
    {
        string[] directories = [Path.Combine(".git", "objects"), ".github", "git", ".git-backup"];

        var actual = LocalScannerDirectoryRules.ContainsGitDirectory(directories);

        Assert.False(actual);
    }
}
