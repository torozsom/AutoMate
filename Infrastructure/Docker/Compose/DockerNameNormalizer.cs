namespace Infrastructure.Docker;

/// <summary>
///     Normalizes external names into Docker-compatible identifiers.
/// </summary>
internal static class DockerNameNormalizer
{
    /// <summary>
    ///     Creates a safe and stable Docker Compose project name.
    /// </summary>
    /// <param name="projectName">The project name used for deployment artifacts or Compose lookup.</param>
    /// <returns>The normalized identity used for the documented routing or lookup.</returns>
    public static string NormalizeProjectName(string projectName)
    {
        var normalized = DockerRegexes.ProjectNameRegex().Replace(projectName.Trim().ToLowerInvariant(), "-");
        normalized = string.Join('-', normalized.Split('-',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return string.IsNullOrWhiteSpace(normalized) ? "automate-project" : normalized;
    }
}
