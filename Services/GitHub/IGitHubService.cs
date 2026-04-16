using Core.DTO;

namespace Services.GitHub;

/// <summary>
///     Interface for GitHub service that defines methods to interact with the GitHub API.
/// </summary>
public interface IGitHubService
{
    /// Retrieves a list of repositories for the authenticated user.
    Task<List<GitHubRepositoryDto>> GetUserRepositoriesAsync(string accessToken, bool forceRefresh = false);
}