using Infrastructure.Docker.Cli;
using Xunit;

namespace Tests.Docker.Cli;

/// <summary>
///     Verifies Compose working-directory and argument construction independently of process execution.
/// </summary>
public sealed class DockerComposeArgumentsTests
{
    private static readonly string[] ExpectedArgs1 = ["compose", "-p", "demo", "up", "--detach"];

    private static readonly string[] ExpectedArgs2 =
        ["compose", "-p", "demo", "--file", "compose preview.yaml", "config"];

    /// <summary>
    ///     Compose receives the selected project before command arguments in their original order.
    /// </summary>
    [Fact]
    public void CreateCompose_CommandArguments_PreservesOrderAndWorkingDirectory()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "compose-unit-input");

        var actual = DockerProcessStartInfoFactory.CreateCompose(workingDirectory, "demo", "up", "--detach");

        Assert.Equal(workingDirectory, actual.WorkingDirectory);
        Assert.Equal(ExpectedArgs1, actual.ArgumentList);
    }

    /// <summary>
    ///     A filename containing spaces remains one argument rather than shell-split command text.
    /// </summary>
    [Fact]
    public void CreateCompose_ArgumentWithSpaces_PreservesSingleArgument()
    {
        var actual =
            DockerProcessStartInfoFactory.CreateCompose(".", "demo", "--file", "compose preview.yaml", "config");

        Assert.Equal(ExpectedArgs2, actual.ArgumentList);
        Assert.Equal(string.Empty, actual.Arguments);
        Assert.False(actual.UseShellExecute);
    }
}
