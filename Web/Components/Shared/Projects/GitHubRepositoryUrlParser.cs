namespace Web.Components.Shared;

/// <summary>
///     Parses persisted GitHub repository URLs into repository references for deployment workflows.
/// </summary>
internal static class GitHubRepositoryUrlParser
{
    /// <summary>
    ///     Attempts to extract the owner and repository name from HTTP(S), SSH, or path-like GitHub repository URLs.
    /// </summary>
    /// <param name="repositoryUrl">The repository URL or owner/name path saved for the application.</param>
    /// <param name="repository">The parsed repository reference when parsing succeeds.</param>
    /// <returns><see langword="true" /> when an owner/name pair could be parsed; otherwise, <see langword="false" />.</returns>
    internal static bool TryParse(string repositoryUrl, out GitHubRepositoryReference repository)
    {
        repository = default;

        if (string.IsNullOrWhiteSpace(repositoryUrl))
            return false;

        var normalized = repositoryUrl.Trim().TrimEnd('/');
        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
            {
                repository = new GitHubRepositoryReference(segments[0], segments[1]);
                return true;
            }
        }

        var sshPrefixIndex = normalized.IndexOf(':', StringComparison.Ordinal);
        if (normalized.StartsWith("git@github.com", StringComparison.OrdinalIgnoreCase) && sshPrefixIndex >= 0)
            normalized = normalized[(sshPrefixIndex + 1)..];

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return false;

        repository = new GitHubRepositoryReference(parts[^2], parts[^1]);
        return true;
    }
}