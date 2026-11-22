using Infrastructure.Docker.Cli;
using Xunit;

namespace Tests.Docker.Cli;

/// <summary>
///     Verifies Docker process options without starting a process.
/// </summary>
public sealed class DockerProcessOptionsTests
{
    /// <summary>
    ///     Default options capture both output streams and disable shell execution and visible windows.
    /// </summary>
    [Fact]
    public void CreateDocker_DefaultOptions_RedirectsStreamsWithoutShell()
    {
        var actual = DockerProcessStartInfoFactory.CreateDocker();

        Assert.Equal("docker", actual.FileName);
        Assert.True(actual.RedirectStandardOutput);
        Assert.True(actual.RedirectStandardError);
        Assert.False(actual.UseShellExecute);
        Assert.True(actual.CreateNoWindow);
        Assert.Empty(actual.ArgumentList);
    }

    /// <summary>
    ///     Disabling stderr redirection does not disable stdout capture or enable shell execution.
    /// </summary>
    [Fact]
    public void CreateDocker_ErrorRedirectionDisabled_PreservesOtherOptions()
    {
        var actual = DockerProcessStartInfoFactory.CreateDocker(false);

        Assert.False(actual.RedirectStandardError);
        Assert.True(actual.RedirectStandardOutput);
        Assert.False(actual.UseShellExecute);
        Assert.True(actual.CreateNoWindow);
    }
}
