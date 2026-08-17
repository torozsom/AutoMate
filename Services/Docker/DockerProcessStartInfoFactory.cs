using System.Diagnostics;

namespace Services.Docker;

/// <summary>
///     Creates Docker CLI process start information with consistent redirection and shell settings.
/// </summary>
internal static class DockerProcessStartInfoFactory
{
    /// <summary>
    ///     Creates start information for a Docker Compose command.
    /// </summary>
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
    public static string Describe(ProcessStartInfo startInfo)
    {
        var arguments = startInfo.ArgumentList.Count > 0
            ? string.Join(' ', startInfo.ArgumentList)
            : startInfo.Arguments;

        return $"{startInfo.FileName} {arguments}".Trim();
    }
}