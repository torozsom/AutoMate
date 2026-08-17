using Core.Defaults;
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
        List<TemplateFile> files, string branchName = DeploymentDefaults.CloudDeploymentBranchName,
        string commitMessage = "Add AutoMate Azure deployment workflow", CancellationToken cancellationToken = default);

    /// <summary>
    ///     Creates or updates GitHub Actions repository secrets used by the generated cloud workflow.
    /// </summary>
    /// <param name="accessToken">The GitHub access token with repository secrets permissions.</param>
    /// <param name="repoOwner">The repository owner or organization.</param>
    /// <param name="repoName">The repository name.</param>
    /// <param name="secrets">The secret name/value pairs to upsert.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    Task UpsertRepositorySecretsAsync(string accessToken, string repoOwner, string repoName,
        IReadOnlyDictionary<string, string> secrets, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Triggers a GitHub Actions workflow on the requested branch.
    /// </summary>
    /// <param name="accessToken">The GitHub access token with workflow permissions.</param>
    /// <param name="repoOwner">The repository owner or organization.</param>
    /// <param name="repoName">The repository name.</param>
    /// <param name="workflowFileName">The workflow file name, for example deploy.yml.</param>
    /// <param name="branchName">The branch that should run the workflow.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    Task DispatchWorkflowAsync(string accessToken, string repoOwner, string repoName, string workflowFileName,
        string branchName, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets the latest GitHub Actions workflow run for a branch.
    /// </summary>
    /// <param name="accessToken">The GitHub access token with workflow read permissions.</param>
    /// <param name="repoOwner">The repository owner or organization.</param>
    /// <param name="repoName">The repository name.</param>
    /// <param name="workflowFileName">The workflow file name, for example deploy.yml.</param>
    /// <param name="branchName">The branch to filter workflow runs by.</param>
    /// <param name="headSha">Optional commit SHA to match.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>The latest matching run, or null when no run exists yet.</returns>
    Task<GitHubWorkflowRunDto?> GetLatestWorkflowRunAsync(string accessToken, string repoOwner, string repoName,
        string workflowFileName, string branchName, string? headSha = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Downloads and flattens GitHub Actions logs for a workflow run.
    /// </summary>
    /// <param name="accessToken">The GitHub access token with workflow read permissions.</param>
    /// <param name="repoOwner">The repository owner or organization.</param>
    /// <param name="repoName">The repository name.</param>
    /// <param name="runId">The GitHub workflow run ID.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>The flattened log text, or null when logs cannot be downloaded.</returns>
    Task<string?> DownloadWorkflowRunLogsAsync(string accessToken, string repoOwner, string repoName, long runId,
        CancellationToken cancellationToken = default);
}