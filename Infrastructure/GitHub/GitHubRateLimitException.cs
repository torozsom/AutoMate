namespace Infrastructure.GitHub;

/// <summary>GitHub asked this installation to pause API requests until the indicated time.</summary>
public sealed class GitHubRateLimitException(DateTimeOffset retryAt)
    : HttpRequestException("GitHub API rate limited the installation.")
{
    /// <summary>Earliest safe retry according to GitHub headers or bounded fallback.</summary>
    public DateTimeOffset RetryAt { get; } = retryAt;
}
