using Infrastructure.Docker.Cli;

namespace Infrastructure.Docker.Compose;

/// <summary>
///     Parses mapped host ports from <c>docker port</c> command output.
/// </summary>
internal static class DockerPortParser
{
    /// <summary>
    ///     Extracts the first host port from Docker CLI port output.
    /// </summary>
    /// <param name="output">Output stream classification or normalized process output.</param>
    /// <returns>The numeric position, count or status described by this operation.</returns>
    public static int ParseHostPort(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return 0;

        var match = DockerRegexes.HostPortRegex().Match(output);
        return match.Success && int.TryParse(match.Groups[1].Value, out var port) ? port : 0;
    }
}
