using Core.DTO;

namespace Services.GitHub;

/// <summary>
///     Interface for GitHub service that defines methods to interact with the GitHub API.
/// </summary>
public interface IGitHubService
{
    /// <summary>
    ///     Retrieves a list of repositories for the authenticated user.
    /// </summary>
    /// <param name="accessToken">The GitHub access token for authentication.</param>
    /// <param name="forceRefresh">Indicates whether to force a refresh of repository data, defaulting to false.</param>
    /// <returns>
    ///     A task that represents the asynchronous operation. The task result contains a list of
    ///     <see cref="GitHubRepositoryDto" />.
    /// </returns>
    Task<List<GitHubRepositoryDto>> GetUserRepositoriesAsync(string accessToken, bool forceRefresh = false);
}