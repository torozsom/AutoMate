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
    Task<List<GitHubRepositoryDto>> GetUserRepositoriesAsync(string accessToken, bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Creates or updates a cloud deployment branch and commits the generated deployment files to it.
    /// </summary>
    /// <param name="accessToken">The GitHub access token with repository write permissions.</param>
    /// <param name="repoOwner">The repository owner or organization.</param>
    /// <param name="repoName">The repository name.</param>
    /// <param name="files">The generated template files to commit.</param>
    /// <param name="branchName">The branch that should receive the deployment commit.</param>
    /// <param name="commitMessage">The commit message to use for the generated files.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>The pushed commit SHA.</returns>
    Task<string> CommitCloudDeploymentFilesAsync(string accessToken, string repoOwner, string repoName,
        List<TemplateFile> files, string branchName = "automate/azure-deployment",
        string commitMessage = "Add AutoMate Azure deployment workflow", CancellationToken cancellationToken = default);
}