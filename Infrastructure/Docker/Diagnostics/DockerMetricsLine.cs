namespace Infrastructure.Docker;

/// <summary>
///     Parsed CPU and memory values from one Docker stats output line.
/// </summary>
/// <param name="Cpu">The CPU usage display value emitted by Docker.</param>
/// <param name="Memory">The memory usage display value emitted by Docker.</param>
internal readonly record struct DockerMetricsLine(string Cpu, string Memory)
{
    /// <summary>
    ///     Parses a Docker stats line formatted as <c>CPU|Memory</c>.
    /// </summary>
    /// <param name="line">One bounded source-output line to normalize and redact.</param>
    /// <param name="metrics">Recorded numeric samples or aggregate points.</param>
    /// <returns>True when validation or acquisition succeeds; otherwise false.</returns>
    public static bool TryParse(string line, out DockerMetricsLine metrics)
    {
        metrics = default;

        if (string.IsNullOrWhiteSpace(line))
            return false;

        line = DockerRegexes.AnsiEscapeRegex().Replace(line, "");
        var parts = line.Split('|');

        if (parts.Length < 2)
            return false;

        metrics = new DockerMetricsLine(parts[0].Trim(), parts[1].Trim());
        return true;
    }
}
