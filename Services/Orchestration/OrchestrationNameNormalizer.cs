using System.Text.RegularExpressions;

namespace Services.Orchestration;

/// <summary>
///     Normalizes deployment names used by Docker Compose, containers, image tags, and cloud resources.
/// </summary>
internal static partial class OrchestrationNameNormalizer
{
    /// <summary>
    ///     Creates a Docker-compatible container name segment.
    /// </summary>
    public static string NormalizeContainerName(string value)
    {
        var normalized = ContainerNameRegex().Replace(value.Trim().ToLowerInvariant(), "-");
        normalized = string.Join('-', normalized.Split('-',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return string.IsNullOrWhiteSpace(normalized) ? "automate-project" : normalized;
    }

    /// <summary>
    ///     Creates a Docker Compose project name using Docker's allowed project-name characters.
    /// </summary>
    public static string NormalizeComposeProjectName(string value)
    {
        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')
            .ToArray());

        normalized = string.Join('-', normalized.Split('-',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return string.IsNullOrWhiteSpace(normalized) ? "automate-project" : normalized;
    }

    /// <summary>
    ///     Creates a stable Docker image tag for a C# project deployment.
    /// </summary>
    public static string GenerateImageTag(string projectName, Guid projectId)
    {
        var safeProjectName = NormalizeContainerName(projectName);
        return $"automate-{safeProjectName}:{projectId.ToString()[..8]}";
    }

    /// <summary>
    ///     Creates an Azure-friendly resource name capped to the existing 23-character limit.
    /// </summary>
    public static string NormalizeResourceName(string value)
    {
        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray());

        normalized = string.Join('-', normalized
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (string.IsNullOrWhiteSpace(normalized))
            normalized = "automate-app";

        return normalized.Length <= 23 ? normalized : normalized[..23].TrimEnd('-');
    }

    /// <summary>
    ///     Matches characters that are not valid in AutoMate's Docker container name convention.
    /// </summary>
    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex ContainerNameRegex();
}