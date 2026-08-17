using System.Text.RegularExpressions;

namespace Services.Docker;

/// <summary>
///     Centralizes generated regular expressions used by Docker parsing helpers.
/// </summary>
internal static partial class DockerRegexes
{
    /// <summary>
    ///     Matches ANSI escape codes emitted by Docker stats in some terminals.
    /// </summary>
    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])")]
    public static partial Regex AnsiEscapeRegex();

    /// <summary>
    ///     Matches the host port portion of <c>docker port</c> output.
    /// </summary>
    [GeneratedRegex(@":(\d+)")]
    public static partial Regex HostPortRegex();

    /// <summary>
    ///     Matches characters that are unsafe in Docker Compose project names.
    /// </summary>
    [GeneratedRegex(@"[^a-z0-9_-]+")]
    public static partial Regex ProjectNameRegex();
}