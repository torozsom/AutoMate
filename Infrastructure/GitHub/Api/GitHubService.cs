using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Application.Abstractions.GitHub;
using Domain.Defaults;
using Domain.DTO;
using Domain.Enums;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Octokit;
using ProductHeaderValue = Octokit.ProductHeaderValue;

namespace Infrastructure.GitHub;

/// <summary>
///     Coordinates GitHub repository, secret, workflow, and log operations for AutoMate
///     deployments.
/// </summary>
public sealed class GitHubService : IGitHubService
{
    /// <summary>Product name sent in GitHub user-agent headers.</summary>
    private const string ProductName = "AutoMate";

    /// <summary>Product version sent in GitHub user-agent headers.</summary>
    private const string ProductVersion = "1.0";

    /// <summary>GitHub REST API base address.</summary>
    private static readonly Uri GitHubApiBaseAddress = new("https://api.github.com/");

    /// <summary>JSON options used for GitHub REST API DTO serialization.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Typed HTTP client configured for GitHub REST API calls.</summary>
    private readonly HttpClient _httpClient;

    /// <summary>Logger for GitHub integration operations.</summary>
    private readonly ILogger<GitHubService> _logger;

    /// <summary>Distributed cache helper for user repository lists.</summary>
    private readonly GitHubRepositoryCache _repositoryCache;


    /// <summary>
    ///     Initializes a new instance of the <see cref="GitHubService" /> class. It configures the
    ///     HttpClient with the base URL and sets the User-Agent header required by GitHub API.
    /// </summary>
    /// <param name="httpClient">The HttpClient instance used for making HTTP requests.</param>
    /// <param name="cache">The IMemoryCache instance used for caching data.</param>
    /// <param name="logger">The logger for the GitHubService class.</param>
    public GitHubService(HttpClient httpClient, IDistributedCache cache, ILogger<GitHubService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _repositoryCache = new GitHubRepositoryCache(cache, logger);

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(ProductName, ProductVersion));

        _httpClient.BaseAddress ??= GitHubApiBaseAddress;
    }

    /// <summary>
    ///     Finds a recent commit containing a durable deployment marker after an uncertain push
    ///     result.
    /// </summary>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="repoOwner">The GitHub repository owner or organization.</param>
    /// <param name="repoName">The GitHub repository name.</param>
    /// <param name="branchName">The repository branch used for deployment assets or workflow selection.</param>
    /// <param name="marker">The stable deployment marker used to find an existing generated commit.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning the matching deployment commit SHA, or null when no marked commit
    ///     exists.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    ///     Unable to verify the prior AutoMate commit in this branch's recent history.
    /// </exception>
    public async Task<string?> FindDeploymentCommitAsync(string accessToken, string repoOwner, string repoName,
        string branchName, string marker, CancellationToken cancellationToken = default)
    {
        for (var page = 1; page <= 10; page++)
        {
            var path = $"repos/{Uri.EscapeDataString(repoOwner)}/{Uri.EscapeDataString(repoName)}/commits" +
                       $"?sha={Uri.EscapeDataString(branchName)}&per_page=100&page={page}";
            using var request = GitHubApiRequestFactory.Create(accessToken, HttpMethod.Get, path);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            GitHubAppCredentials.ThrowIfRateLimited(response);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var commits = payload.RootElement.EnumerateArray().ToArray();
            foreach (var item in commits)
            {
                if (!item.TryGetProperty("commit", out var commit) ||
                    !commit.TryGetProperty("message", out var message) ||
                    !message.GetString()!.Contains(marker, StringComparison.Ordinal)) continue;
                return item.GetProperty("sha").GetString();
            }

            if (commits.Length < 100) return null;
        }

        throw new InvalidOperationException(
            "Unable to verify the prior AutoMate commit in this branch's recent history.");
    }


    /// <summary>
    ///     Asynchronously retrieves the list of repositories for the authenticated user from
    ///     GitHub.
    /// </summary>
    /// <param name="accessToken">The access token of the authenticated user.</param>
    /// <param name="forceRefresh">
    ///     A flag indicating whether to force a refresh of the repository list, bypassing the
    ///     cache.
    /// </param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>
    ///     A task returning a list of GitHubRepositoryDto objects representing the user's
    ///     repositories.
    /// </returns>
    public async Task<List<GitHubRepositoryDto>> GetUserRepositoriesAsync(string accessToken, bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            _logger.LogWarning("[GitHubService] Attempted to get GitHub repositories with an empty access token.");
            return [];
        }

        var cachedRepositories = await _repositoryCache.TryGetAsync(accessToken, forceRefresh, JsonOptions,
            cancellationToken);
        if (cachedRepositories != null)
            return cachedRepositories;

        try
        {
            _logger.LogInformation("[GitHubService] Fetching repositories from GitHub API...");

            using var requestMessage = GitHubApiRequestFactory.Create(accessToken, HttpMethod.Get,
                "user/repos?sort=updated&per_page=100");

            using var response = await _httpClient.SendAsync(requestMessage, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "[GitHubService] GitHub API returned error status code: {StatusCode} for getting repositories.",
                    response.StatusCode);
                return [];
            }

            var repositories =
                await response.Content.ReadFromJsonAsync<List<GitHubRepositoryDto>>(JsonOptions, cancellationToken) ??
                [];

            await _repositoryCache.TrySetAsync(accessToken, repositories, JsonOptions, cancellationToken);

            _logger.LogInformation(
                "[GitHubService] Successfully retrieved and cached {Count} repositories from GitHub API.",
                repositories.Count);
            return repositories;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[GitHubService] Fetching repositories from GitHub API was cancelled.");
            return [];
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError("GitHub API network request failed: {FailureType}.", ex.GetType().Name);
            return [];
        }
    }


    /// <summary>
    ///     Creates or updates a cloud deployment branch and commits the generated deployment files
    ///     to it.
    /// </summary>
    /// <param name="accessToken">The GitHub access token with repository write permissions.</param>
    /// <param name="repoOwner">The repository owner or organization.</param>
    /// <param name="repoName">The repository name.</param>
    /// <param name="files">The generated template files to commit.</param>
    /// <param name="branchName">The branch that should receive the deployment commit.</param>
    /// <param name="commitMessage">The commit message to use for the generated files.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A task returning the pushed commit SHA.</returns>
    /// <exception cref="ArgumentException">GitHub access token is required.</exception>
    /// <exception cref="GitHubRateLimitException">
    ///     The deployment file selection is invalid or GitHub throttles the commit request.
    /// </exception>
    public async Task<string> CommitCloudDeploymentFilesAsync(string accessToken, string repoOwner, string repoName,
        List<TemplateFile> files, string branchName = DeploymentDefaults.CloudDeploymentBranchName,
        string commitMessage = "Add AutoMate Azure deployment workflow", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("GitHub access token is required.", nameof(accessToken));

        if (string.IsNullOrWhiteSpace(repoOwner))
            throw new ArgumentException("Repository owner is required.", nameof(repoOwner));

        if (string.IsNullOrWhiteSpace(repoName))
            throw new ArgumentException("Repository name is required.", nameof(repoName));

        if (files is null || files.Count == 0)
            throw new ArgumentException("At least one file is required for a cloud deployment commit.", nameof(files));

        if (string.IsNullOrWhiteSpace(branchName))
            throw new ArgumentException("Branch name is required.", nameof(branchName));

        if (string.IsNullOrWhiteSpace(commitMessage))
            throw new ArgumentException("Commit message is required.", nameof(commitMessage));

        cancellationToken.ThrowIfCancellationRequested();

        var gitHubClient = CreateGitHubClient(accessToken);

        try
        {
            var repository = await gitHubClient.Repository.Get(repoOwner, repoName);
            cancellationToken.ThrowIfCancellationRequested();

            var baseBranch = repository.DefaultBranch;
            var baseReference = await gitHubClient.Git.Reference.Get(repoOwner, repoName, $"heads/{baseBranch}");
            var targetReference = await GetOrCreateBranchReferenceAsync(gitHubClient, repoOwner, repoName, branchName,
                baseReference.Object.Sha, cancellationToken);

            var targetCommit = await gitHubClient.Git.Commit.Get(repoOwner, repoName, targetReference.Object.Sha);
            var tree = new NewTree { BaseTree = targetCommit.Tree.Sha };

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(file.Path))
                    throw new ArgumentException("Generated files must have non-empty paths.", nameof(files));

                tree.Tree.Add(new NewTreeItem
                {
                    Path = file.Path.Replace('\\', '/'),
                    Mode = "100644",
                    Type = TreeType.Blob,
                    Content = file.Content
                });
            }

            var createdTree = await gitHubClient.Git.Tree.Create(repoOwner, repoName, tree);
            var commit = new NewCommit(commitMessage, createdTree.Sha, targetReference.Object.Sha);
            var createdCommit = await gitHubClient.Git.Commit.Create(repoOwner, repoName, commit);

            await gitHubClient.Git.Reference.Update(repoOwner, repoName, $"heads/{branchName}",
                new ReferenceUpdate(createdCommit.Sha));

            _logger.LogInformation("Committed {FileCount} cloud deployment files to GitHub.", files.Count);

            return createdCommit.Sha;
        }
        catch (RateLimitExceededException ex)
        {
            throw new GitHubRateLimitException(ex.Reset);
        }
        catch (AbuseException)
        {
            throw new GitHubRateLimitException(DateTimeOffset.UtcNow.AddMinutes(1));
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("GitHub cloud deployment commit was canceled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("GitHub cloud deployment commit failed: {FailureType}.", ex.GetType().Name);
            throw;
        }
    }


    /// <summary>
    ///     Creates or updates GitHub Actions repository secrets used by the generated cloud
    ///     workflow.
    /// </summary>
    /// <param name="accessToken">The GitHub access token with repository secrets permissions.</param>
    /// <param name="repoOwner">The repository owner or organization.</param>
    /// <param name="repoName">The repository name.</param>
    /// <param name="secrets">The secret name/value pairs to upsert.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A task that completes after the encrypted repository secrets are submitted.</returns>
    /// <exception cref="ArgumentException">GitHub access token is required.</exception>
    /// <exception cref="InvalidOperationException">GitHub repository public key could not be loaded.</exception>
    /// <exception cref="ArgumentNullException">A required input or dependency is null.</exception>
    /// <exception cref="OperationCanceledException">The supplied cancellation token is canceled.</exception>
    public async Task UpsertRepositorySecretsAsync(string accessToken, string repoOwner, string repoName,
        IReadOnlyDictionary<string, string> secrets, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("GitHub access token is required.", nameof(accessToken));

        if (string.IsNullOrWhiteSpace(repoOwner))
            throw new ArgumentException("Repository owner is required.", nameof(repoOwner));

        if (string.IsNullOrWhiteSpace(repoName))
            throw new ArgumentException("Repository name is required.", nameof(repoName));

        ArgumentNullException.ThrowIfNull(secrets);

        if (secrets.Count == 0)
            return;

        using var publicKeyRequest = GitHubApiRequestFactory.Create(accessToken, HttpMethod.Get,
            $"repos/{repoOwner}/{repoName}/actions/secrets/public-key");
        using var publicKeyResponse = await _httpClient.SendAsync(publicKeyRequest, cancellationToken);
        GitHubAppCredentials.ThrowIfRateLimited(publicKeyResponse);
        publicKeyResponse.EnsureSuccessStatusCode();

        var publicKey = await publicKeyResponse.Content.ReadFromJsonAsync<GitHubRepositoryPublicKey>(JsonOptions,
            cancellationToken);

        if (publicKey == null || string.IsNullOrWhiteSpace(publicKey.Key) || string.IsNullOrWhiteSpace(publicKey.KeyId))
            throw new InvalidOperationException("GitHub repository public key could not be loaded.");

        foreach (var (secretName, secretValue) in secrets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(secretName))
                throw new ArgumentException("GitHub repository secret names cannot be empty.", nameof(secrets));

            var encryptedValue = GitHubSecretEncryptor.EncryptSecret(secretValue, publicKey.Key);

            using var request = GitHubApiRequestFactory.Create(accessToken, HttpMethod.Put,
                $"repos/{repoOwner}/{repoName}/actions/secrets/{secretName}");

            request.Content = JsonContent.Create(new GitHubRepositorySecretRequest(encryptedValue, publicKey.KeyId),
                options: JsonOptions);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            GitHubAppCredentials.ThrowIfRateLimited(response);
            response.EnsureSuccessStatusCode();

            _logger.LogInformation("GitHub repository secret upserted.");
        }
    }


    /// <summary>
    ///     Triggers a GitHub Actions workflow on the requested branch.
    /// </summary>
    /// <param name="accessToken">The GitHub access token with workflow permissions.</param>
    /// <param name="repoOwner">The repository owner or organization.</param>
    /// <param name="repoName">The repository name.</param>
    /// <param name="workflowFileName">The workflow file name, for example deploy.yml.</param>
    /// <param name="branchName">The branch that should run the workflow.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A task that completes after the workflow dispatch request is accepted.</returns>
    /// <exception cref="ArgumentException">GitHub access token is required.</exception>
    public async Task DispatchWorkflowAsync(string accessToken, string repoOwner, string repoName,
        string workflowFileName, string branchName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("GitHub access token is required.", nameof(accessToken));

        if (string.IsNullOrWhiteSpace(repoOwner))
            throw new ArgumentException("Repository owner is required.", nameof(repoOwner));

        if (string.IsNullOrWhiteSpace(repoName))
            throw new ArgumentException("Repository name is required.", nameof(repoName));

        if (string.IsNullOrWhiteSpace(workflowFileName))
            throw new ArgumentException("Workflow file name is required.", nameof(workflowFileName));

        if (string.IsNullOrWhiteSpace(branchName))
            throw new ArgumentException("Branch name is required.", nameof(branchName));

        using var request = GitHubApiRequestFactory.Create(accessToken, HttpMethod.Post,
            $"repos/{repoOwner}/{repoName}/actions/workflows/{workflowFileName}/dispatches");

        request.Content = JsonContent.Create(new GitHubWorkflowDispatchRequest(branchName), options: JsonOptions);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        GitHubAppCredentials.ThrowIfRateLimited(response);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("GitHub workflow dispatched.");
    }


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
    /// <returns>A task returning the latest matching run, or null when no run exists yet.</returns>
    /// <exception cref="ArgumentException">Workflow file name is required.</exception>
    public async Task<GitHubWorkflowRunDto?> GetLatestWorkflowRunAsync(string accessToken, string repoOwner,
        string repoName, string workflowFileName, string branchName, string? headSha = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workflowFileName))
            throw new ArgumentException("Workflow file name is required.", nameof(workflowFileName));

        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("GitHub access token is required.", nameof(accessToken));

        if (string.IsNullOrWhiteSpace(repoOwner))
            throw new ArgumentException("Repository owner is required.", nameof(repoOwner));

        if (string.IsNullOrWhiteSpace(repoName))
            throw new ArgumentException("Repository name is required.", nameof(repoName));

        if (string.IsNullOrWhiteSpace(branchName))
            throw new ArgumentException("Branch name is required.", nameof(branchName));

        using var request = GitHubApiRequestFactory.Create(accessToken, HttpMethod.Get,
            $"repos/{repoOwner}/{repoName}/actions/runs?branch={Uri.EscapeDataString(branchName)}&per_page=20");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        GitHubAppCredentials.ThrowIfRateLimited(response);
        response.EnsureSuccessStatusCode();

        var runsResponse = await response.Content.ReadFromJsonAsync<GitHubWorkflowRunsResponse>(JsonOptions,
            cancellationToken);

        return GitHubWorkflowRunMapper.SelectLatestMatchingRun(runsResponse?.WorkflowRuns ?? [], workflowFileName,
            headSha);
    }


    /// <summary>
    ///     Downloads the final workflow archive as individually named log entries.
    /// </summary>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="repoOwner">The GitHub repository owner or organization.</param>
    /// <param name="repoName">The GitHub repository name.</param>
    /// <param name="runId">The GitHub workflow-run identity.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning the normalized bounded entries from the completed-run log archive.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     The workflow-run identifier is not positive.
    /// </exception>
    public async Task<IReadOnlyList<GitHubWorkflowLogArchiveEntryDto>> DownloadWorkflowRunLogEntriesAsync(
        string accessToken, string repoOwner, string repoName, long runId,
        CancellationToken cancellationToken = default)
    {
        if (runId <= 0) throw new ArgumentOutOfRangeException(nameof(runId));

        using var request = GitHubApiRequestFactory.Create(accessToken, HttpMethod.Get,
            $"repos/{repoOwner}/{repoName}/actions/runs/{runId}/logs");
        using var response = await SendLogDownloadRequestAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return [];

        await using var zipStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await GitHubWorkflowLogReader.ReadEntriesAsync(zipStream, cancellationToken);
    }

    /// <summary>
    ///     Lists jobs and steps for one workflow run.
    /// </summary>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="repoOwner">The GitHub repository owner or organization.</param>
    /// <param name="repoName">The GitHub repository name.</param>
    /// <param name="runId">The GitHub workflow-run identity.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning the workflow's discovered jobs and steps.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     The workflow-run identifier is not positive.
    /// </exception>
    public async Task<IReadOnlyList<GitHubWorkflowJobDto>> GetWorkflowJobsAsync(string accessToken, string repoOwner,
        string repoName, long runId, CancellationToken cancellationToken = default)
    {
        if (runId <= 0) throw new ArgumentOutOfRangeException(nameof(runId));

        var jobs = new List<GitHubWorkflowJobItem>();
        for (var page = 1; page <= 10; page++)
        {
            using var request = GitHubApiRequestFactory.Create(accessToken, HttpMethod.Get,
                $"repos/{repoOwner}/{repoName}/actions/runs/{runId}/jobs?filter=latest&per_page=100&page={page}");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            GitHubAppCredentials.ThrowIfRateLimited(response);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadFromJsonAsync<GitHubWorkflowJobsResponse>(JsonOptions,
                cancellationToken);
            var pageJobs = responseBody?.Jobs ?? [];
            jobs.AddRange(pageJobs);
            if (pageJobs.Count < 100) break;
        }

        return GitHubWorkflowJobMapper.Map(jobs);
    }

    /// <summary>
    ///     Downloads a plain-text log for one workflow job when GitHub makes it available.
    /// </summary>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="repoOwner">The GitHub repository owner or organization.</param>
    /// <param name="repoName">The GitHub repository name.</param>
    /// <param name="jobId">The GitHub workflow job identity.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning the bounded download result and its finite log-availability state.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     The workflow-job identifier is not positive.
    /// </exception>
    public async Task<GitHubWorkflowJobLogDownload> DownloadWorkflowJobLogsAsync(string accessToken, string repoOwner,
        string repoName, long jobId, CancellationToken cancellationToken = default)
    {
        if (jobId <= 0) throw new ArgumentOutOfRangeException(nameof(jobId));

        using var request = GitHubApiRequestFactory.Create(accessToken, HttpMethod.Get,
            $"repos/{repoOwner}/{repoName}/actions/jobs/{jobId}/logs");
        using var response = await SendLogDownloadRequestAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            await using var zipStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var zipLogText = await GitHubWorkflowLogReader.ReadTextAsync(zipStream, cancellationToken);
            return new GitHubWorkflowJobLogDownload(GitHubWorkflowLogAvailability.Available, zipLogText);
        }

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict or
            HttpStatusCode.UnprocessableEntity)
            return new GitHubWorkflowJobLogDownload(GitHubWorkflowLogAvailability.NotAvailable);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new GitHubWorkflowJobLogDownload(GitHubWorkflowLogAvailability.AccessDenied);

        _logger.LogWarning("GitHub returned {StatusCode} while downloading workflow job logs for job {JobId}.",
            response.StatusCode, jobId);
        return new GitHubWorkflowJobLogDownload(GitHubWorkflowLogAvailability.Failed);
    }

    /// <summary>
    ///     Follows the short-lived, signed download location returned by GitHub Actions without
    ///     forwarding OAuth credentials to the storage host. Automatic redirect behavior varies by
    ///     handler and must not control log collection.
    /// </summary>
    /// <param name="request">Configured bounded provider request; authorization must not be logged.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning the http response message projection derived from the verified inputs.
    /// </returns>
    private async Task<HttpResponseMessage> SendLogDownloadRequestAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        GitHubAppCredentials.ThrowIfRateLimited(response);
        var redirectLocation = response.Headers.Location;
        if ((int)response.StatusCode is < 300 or >= 400 || redirectLocation is null)
            return response;

        var downloadUri = redirectLocation.IsAbsoluteUri
            ? redirectLocation
            : new Uri(response.RequestMessage?.RequestUri ?? GitHubApiBaseAddress, redirectLocation);
        response.Dispose();

        using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, downloadUri);
        return await _httpClient.SendAsync(downloadRequest, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }


    /// <summary>
    ///     Returns an existing branch reference or creates it from the supplied base SHA.
    /// </summary>
    /// <param name="gitHubClient">
    ///     Authorized GitHub client scoped to the initiating account or verified installation.
    /// </param>
    /// <param name="repoOwner">The GitHub repository owner or organization.</param>
    /// <param name="repoName">The GitHub repository name.</param>
    /// <param name="branchName">The repository branch used for deployment assets or workflow selection.</param>
    /// <param name="baseSha">Commit SHA used as the parent of the generated commit.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning the reference projection derived from the verified inputs.</returns>
    /// <exception cref="OperationCanceledException">The supplied cancellation token is canceled.</exception>
    private static async Task<Reference> GetOrCreateBranchReferenceAsync(GitHubClient gitHubClient, string repoOwner,
        string repoName, string branchName, string baseSha, CancellationToken cancellationToken)
    {
        try
        {
            return await gitHubClient.Git.Reference.Get(repoOwner, repoName, $"heads/{branchName}");
        }
        catch (NotFoundException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await gitHubClient.Git.Reference.Create(repoOwner, repoName,
                new NewReference($"refs/heads/{branchName}", baseSha));
        }
    }

    /// <summary>
    ///     Creates an Octokit client authenticated as the connected GitHub user.
    /// </summary>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <returns>The git hub client projection derived from the verified inputs.</returns>
    private static GitHubClient CreateGitHubClient(string accessToken)
    {
        return new GitHubClient(new ProductHeaderValue(ProductName))
        {
            Credentials = new Credentials(accessToken)
        };
    }
}
