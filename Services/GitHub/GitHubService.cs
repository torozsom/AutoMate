using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Core.Defaults;
using Core.DTO;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Octokit;
using ProductHeaderValue = Octokit.ProductHeaderValue;

namespace Services.GitHub;

/// <summary>
///     Coordinates GitHub repository, secret, workflow, and log operations for AutoMate deployments.
/// </summary>
public sealed class GitHubService : IGitHubService
{
    /// <summary>
    ///     Product name sent in GitHub user-agent headers.
    /// </summary>
    private const string ProductName = "AutoMate";

    /// <summary>
    ///     Product version sent in GitHub user-agent headers.
    /// </summary>
    private const string ProductVersion = "1.0";

    /// <summary>
    ///     GitHub REST API base address.
    /// </summary>
    private static readonly Uri GitHubApiBaseAddress = new("https://api.github.com/");

    /// <summary>
    ///     JSON options used for GitHub REST API DTO serialization.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    ///     Typed HTTP client configured for GitHub REST API calls.
    /// </summary>
    private readonly HttpClient _httpClient;

    /// <summary>
    ///     Logger for GitHub integration operations.
    /// </summary>
    private readonly ILogger<GitHubService> _logger;

    /// <summary>
    ///     Distributed cache helper for user repository lists.
    /// </summary>
    private readonly GitHubRepositoryCache _repositoryCache;


    /// <summary>
    ///     Initializes a new instance of the <see cref="GitHubService" /> class.
    ///     It configures the HttpClient with the base URL and sets the User-Agent header required by GitHub API.
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


    /// <inheritdoc />
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
            _logger.LogError(ex, "[GitHubService] Network error occurred while contacting the GitHub API.");
            return [];
        }
    }


    /// <inheritdoc />
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

            _logger.LogInformation(
                "[GitHubService] Committed {FileCount} cloud deployment files to {Owner}/{Repo}@{Branch}. Commit: {Sha}",
                files.Count, repoOwner, repoName, branchName, createdCommit.Sha);

            return createdCommit.Sha;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "[GitHubService] Cloud deployment commit was cancelled for {Owner}/{Repo}@{Branch}.",
                repoOwner, repoName, branchName);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[GitHubService] Failed to commit cloud deployment files to {Owner}/{Repo}@{Branch}.",
                repoOwner, repoName, branchName);
            throw;
        }
    }


    /// <inheritdoc />
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
            response.EnsureSuccessStatusCode();

            _logger.LogInformation("[GitHubService] Upserted repository secret {SecretName} for {Owner}/{Repo}.",
                secretName, repoOwner, repoName);
        }
    }


    /// <inheritdoc />
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
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("[GitHubService] Dispatched workflow {Workflow} for {Owner}/{Repo}@{Branch}.",
            workflowFileName, repoOwner, repoName, branchName);
    }


    /// <inheritdoc />
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
        response.EnsureSuccessStatusCode();

        var runsResponse = await response.Content.ReadFromJsonAsync<GitHubWorkflowRunsResponse>(JsonOptions,
            cancellationToken);

        return GitHubWorkflowRunMapper.SelectLatestMatchingRun(runsResponse?.WorkflowRuns ?? [], workflowFileName,
            headSha);
    }


    /// <inheritdoc />
    public async Task<string?> DownloadWorkflowRunLogsAsync(string accessToken, string repoOwner, string repoName,
        long runId, CancellationToken cancellationToken = default)
    {
        if (runId <= 0)
            throw new ArgumentOutOfRangeException(nameof(runId), "Workflow run ID must be a positive number.");

        using var request = GitHubApiRequestFactory.Create(accessToken, HttpMethod.Get,
            $"repos/{repoOwner}/{repoName}/actions/runs/{runId}/logs");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "[GitHubService] GitHub returned {StatusCode} while downloading workflow logs for run {RunId}.",
                response.StatusCode, runId);
            return null;
        }

        await using var zipStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await GitHubWorkflowLogReader.ReadFlattenedLogsAsync(zipStream, cancellationToken);
    }


    /// <summary>
    ///     Returns an existing branch reference or creates it from the supplied base SHA.
    /// </summary>
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
    private static GitHubClient CreateGitHubClient(string accessToken)
    {
        return new GitHubClient(new ProductHeaderValue(ProductName))
        {
            Credentials = new Credentials(accessToken)
        };
    }
}