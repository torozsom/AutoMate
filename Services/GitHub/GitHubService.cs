using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Core.DTO;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Octokit;
using Sodium;
using ProductHeaderValue = Octokit.ProductHeaderValue;

namespace Services.GitHub;

/// <summary>
///     Service class responsible for interacting with the GitHub API
///     to retrieve user repositories and other related data.
/// </summary>
public class GitHubService : IGitHubService
{
    private static readonly TimeSpan RepositoryCacheTtl = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IDistributedCache _cache;
    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubService> _logger;


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
        _cache = cache;
        _logger = logger;

        if (!(_httpClient.DefaultRequestHeaders.UserAgent.Count > 0))
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AutoMate", "1.0"));

        _httpClient.BaseAddress ??= new Uri("https://api.github.com/");
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

        // Generate a cache key based on the access token to ensure that cached data is user-specific and secure.
        var cacheKey = GenerateCacheKey(accessToken);

        if (!forceRefresh)
            try
            {
                // Attempt to retrieve the repository list from the distributed cache using the generated cache key.
                var cachedJson = await _cache.GetStringAsync(cacheKey, cancellationToken);
                if (!string.IsNullOrEmpty(cachedJson))
                {
                    var cachedRepos = JsonSerializer.Deserialize<List<GitHubRepositoryDto>>(cachedJson, JsonOptions);
                    if (cachedRepos != null)
                    {
                        _logger.LogInformation(
                            "[GitHubService] Successfully retrieved {Count} GitHub repositories from cache.",
                            cachedRepos.Count);
                        return cachedRepos;
                    }
                }
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(
                    "[GitHubService] Fetching repositories from cache was cancelled. Exception: {Message}", ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[GitHubService] Failed to read or deserialize repositories from cache. Falling back to API call.");
            }

        try
        {
            _logger.LogInformation("[GitHubService] Fetching repositories from GitHub API...");

            // Create an HTTP GET request to the GitHub API endpoint for user repositories.
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, "user/repos?sort=updated&per_page=100");
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            // Send the HTTP request and await the response.
            using var response = await _httpClient.SendAsync(requestMessage, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "[GitHubService] GitHub API returned error status code: {StatusCode} for getting repositories.",
                    response.StatusCode);
                return [];
            }

            // Read and deserialize the response content into a list of GitHubRepositoryDto objects.
            var repositories =
                await response.Content.ReadFromJsonAsync<List<GitHubRepositoryDto>>(JsonOptions, cancellationToken) ??
                [];

            try
            {
                // Cache the retrieved repository list in the distributed cache for 10 minutes.
                var cacheOptions = new DistributedCacheEntryOptions
                    { AbsoluteExpirationRelativeToNow = RepositoryCacheTtl };
                var serializedRepos = JsonSerializer.Serialize(repositories, JsonOptions);

                // Store the serialized repository list in the distributed cache using the generated cache key and cache options.
                await _cache.SetStringAsync(cacheKey, serializedRepos, cacheOptions, cancellationToken);
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(
                    "[GitHubService] Failed to save repositories to distributed cache. Exception: {Message}",
                    ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GitHubService] Failed to save repositories to distributed cache.");
            }

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
        List<TemplateFile> files, string branchName = "automate/azure-deployment",
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

        var gitHubClient = new GitHubClient(new ProductHeaderValue("AutoMate"))
        {
            Credentials = new Credentials(accessToken)
        };

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

        using var publicKeyRequest = CreateGitHubRequest(accessToken, HttpMethod.Get,
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

            var encryptedValue = EncryptSecret(secretValue, publicKey.Key);

            using var request = CreateGitHubRequest(accessToken, HttpMethod.Put,
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

        using var request = CreateGitHubRequest(accessToken, HttpMethod.Post,
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

        using var request = CreateGitHubRequest(accessToken, HttpMethod.Get,
            $"repos/{repoOwner}/{repoName}/actions/runs?branch={Uri.EscapeDataString(branchName)}&per_page=20");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var runsResponse = await response.Content.ReadFromJsonAsync<GitHubWorkflowRunsResponse>(JsonOptions,
            cancellationToken);

        var workflowRuns = runsResponse?.WorkflowRuns ?? [];
        var matchingRuns = workflowRuns
            .Where(r => string.IsNullOrWhiteSpace(headSha) ||
                        string.Equals(r.HeadSha, headSha, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var workflowFilePathSuffix = $"/{workflowFileName}";
        var preferredRuns = matchingRuns
            .Where(r => string.IsNullOrWhiteSpace(workflowFileName) ||
                        r.Path?.EndsWith(workflowFilePathSuffix, StringComparison.OrdinalIgnoreCase) == true ||
                        string.Equals(r.Path, workflowFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (preferredRuns.Count == 0)
            preferredRuns = matchingRuns;

        return preferredRuns
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new GitHubWorkflowRunDto
            {
                Id = r.Id,
                Status = r.Status,
                Conclusion = r.Conclusion,
                HtmlUrl = r.HtmlUrl,
                HeadSha = r.HeadSha,
                HeadBranch = r.HeadBranch
            })
            .FirstOrDefault();
    }


    /// <inheritdoc />
    public async Task<string?> DownloadWorkflowRunLogsAsync(string accessToken, string repoOwner, string repoName,
        long runId, CancellationToken cancellationToken = default)
    {
        if (runId <= 0)
            throw new ArgumentOutOfRangeException(nameof(runId), "Workflow run ID must be a positive number.");

        using var request = CreateGitHubRequest(accessToken, HttpMethod.Get,
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
        await using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var builder = new StringBuilder();

        foreach (var entry in archive.Entries
                     .Where(e => !string.IsNullOrWhiteSpace(e.Name))
                     .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            builder.AppendLine();
            builder.AppendLine($"===== {entry.FullName} =====");

            await using var entryStream = await entry.OpenAsync(cancellationToken);
            using var reader = new StreamReader(entryStream, Encoding.UTF8, true);
            var content = await reader.ReadToEndAsync(cancellationToken);
            builder.AppendLine(content);
        }

        return builder.Length == 0 ? null : builder.ToString();
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
    ///     Creates an HttpRequestMessage for GitHub API calls, setting the
    ///     appropriate headers for authentication and content type.
    /// </summary>
    /// <param name="accessToken">GitHub access token for authentication.</param>
    /// <param name="method">HTTP method for the request.</param>
    /// <param name="requestUri">The URI for the GitHub API endpoint.</param>
    /// <returns>An HttpRequestMessage configured for the GitHub API call.</returns>
    /// <exception cref="ArgumentException">Thrown if the accessToken is null or whitespace.</exception>
    private static HttpRequestMessage CreateGitHubRequest(string accessToken, HttpMethod method, string requestUri)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("GitHub access token is required.", nameof(accessToken));

        var escapedRequestUri = EscapeGitHubRequestUri(requestUri);
        var request = new HttpRequestMessage(method, escapedRequestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }


    /// <summary>
    ///     Escapes the path segments of a GitHub API request URI while preserving query parameters.
    /// </summary>
    /// <param name="requestUri">The URI to escape.</param>
    /// <returns>The escaped URI.</returns>
    private static string EscapeGitHubRequestUri(string requestUri)
    {
        var queryStart = requestUri.IndexOf('?');
        var path = queryStart >= 0 ? requestUri[..queryStart] : requestUri;
        var query = queryStart >= 0 ? requestUri[queryStart..] : string.Empty;

        var escapedPath = string.Join('/',
            path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));

        return $"{escapedPath}{query}";
    }


    /// <summary>
    ///     Encrypts a secret value using the repository's public key with the NaCl sealed box algorithm.
    /// </summary>
    /// <param name="secretValue">The secret value to encrypt.</param>
    /// <param name="base64PublicKey">The base64-encoded public key.</param>
    /// <returns>The encrypted secret value.</returns>
    private static string EncryptSecret(string secretValue, string base64PublicKey)
    {
        var secretBytes = Encoding.UTF8.GetBytes(secretValue);
        var publicKeyBytes = Convert.FromBase64String(base64PublicKey);
        var encryptedBytes = SealedPublicKeyBox.Create(secretBytes, publicKeyBytes);
        return Convert.ToBase64String(encryptedBytes);
    }


    /// <summary>
    ///     Generates a cache key based on the provided access token by
    ///     hashing it using SHA256 and encoding it in a URL-safe format.
    /// </summary>
    /// <param name="token">The token to be hashed.</param>
    /// <returns>The safe cache key.</returns>
    private static string GenerateCacheKey(string token)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var safeHash = Convert.ToBase64String(hashBytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');

        return $"github_repos_{safeHash}";
    }


    /// Record class for representing a GitHub repository public key.
    private sealed record GitHubRepositoryPublicKey(
        [property: JsonPropertyName("key_id")] string KeyId,
        [property: JsonPropertyName("key")] string Key);


    /// Record class for representing a request to create or update a GitHub repository secret.
    private sealed record GitHubRepositorySecretRequest(
        [property: JsonPropertyName("encrypted_value")]
        string EncryptedValue,
        [property: JsonPropertyName("key_id")] string KeyId);


    /// Record class for representing a request to dispatch a GitHub workflow.
    private sealed record GitHubWorkflowDispatchRequest([property: JsonPropertyName("ref")] string Ref);


    /// Record classes for representing the response from GitHub API when fetching workflow runs.
    private sealed record GitHubWorkflowRunsResponse(
        [property: JsonPropertyName("workflow_runs")]
        List<GitHubWorkflowRunItem> WorkflowRuns);


    /// Record class for representing a GitHub workflow run item in the response.
    private sealed record GitHubWorkflowRunItem(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("conclusion")]
        string? Conclusion,
        [property: JsonPropertyName("html_url")]
        string HtmlUrl,
        [property: JsonPropertyName("head_sha")]
        string HeadSha,
        [property: JsonPropertyName("head_branch")]
        string HeadBranch,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("created_at")]
        DateTimeOffset CreatedAt);
}