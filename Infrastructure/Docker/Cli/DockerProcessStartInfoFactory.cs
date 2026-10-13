using System.Diagnostics;

namespace Infrastructure.Docker;

/// <summary>
///     Creates Docker CLI process start information with consistent redirection and shell
///     settings.
/// </summary>
internal static class DockerProcessStartInfoFactory
{
    /// <summary>
    ///     Creates start information for a Docker Compose command.
    /// </summary>
    /// <param name="workingDir">The local directory containing the Compose deployment.</param>
    /// <param name="safeProjectName">Normalized non-secret artifact project name.</param>
    /// <param name="composeArguments">Bounded argument list passed to the Docker Compose process.</param>
    /// <returns>Docker process arguments and stream settings without launching a process.</returns>
    public static ProcessStartInfo CreateCompose(string workingDir, string safeProjectName,
        params string[] composeArguments)
    {
        var startInfo = CreateDocker();
        startInfo.WorkingDirectory = workingDir;
        startInfo.ArgumentList.Add("compose");
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(safeProjectName);

        foreach (var argument in composeArguments)
            startInfo.ArgumentList.Add(argument);

        return startInfo;
    }

    /// <summary>
    ///     Creates start information for the Docker CLI.
    /// </summary>
    /// <param name="redirectStandardError">
    ///     Whether the owned process exposes a separate error-output stream.
    /// </param>
    /// <returns>Docker process arguments and stream settings without launching a process.</returns>
    public static ProcessStartInfo CreateDocker(bool redirectStandardError = true)
    {
        return new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = redirectStandardError,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    /// <summary>
    ///     Creates a human-readable process command for logs.
    /// </summary>
    /// <param name="startInfo">Process arguments and stream settings for the owned Docker command.</param>
    /// <returns>The describe derived from the validated input.</returns>
    public static string Describe(ProcessStartInfo startInfo)
    {
        var arguments = startInfo.ArgumentList.Count > 0
            ? string.Join(' ', startInfo.ArgumentList)
            : startInfo.Arguments;

        return $"{startInfo.FileName} {arguments}".Trim();
    }
}
