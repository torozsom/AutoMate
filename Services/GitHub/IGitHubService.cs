using Core.DTO;

namespace Services.GitHub;

/// <summary>
///     Service interface for interacting with the GitHub API.
/// </summary>
public interface IGitHubService
{
    /// <summary>
    ///     Asynchronously retrieves the list of repositories for the authenticated user from GitHub.
    /// </summary>
    /// <param name="accessToken">The access token of the authenticated user.</param>
    /// <param name="forceRefresh">A flag indicating whether to force a refresh of the repository list, bypassing the cache.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A list of GitHubRepositoryDto objects representing the user's repositories.</returns>
    Task<List<GitHubRepositoryDto>> GetUserRepositoriesAsync(string accessToken, bool forceRefresh = false, CancellationToken cancellationToken = default);
}