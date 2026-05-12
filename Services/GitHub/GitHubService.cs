using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core.DTO;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Services.GitHub;

/// <summary>
///     Service class responsible for interacting with the GitHub API
///     to retrieve user repositories and other related data.
/// </summary>
public class GitHubService : IGitHubService
{
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
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AutoMate", "1.0"));
        }

        _httpClient.BaseAddress ??= new Uri("https://api.github.com/");
    }


    /// <inheritdoc/>
    public async Task<List<GitHubRepositoryDto>> GetUserRepositoriesAsync(string accessToken, bool forceRefresh, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            _logger.LogWarning("[GitHubService] Attempted to get GitHub repositories with an empty access token.");
            return [];
        }

        // Generate a cache key based on the access token to ensure that cached data is user-specific and secure.
        var cacheKey = GenerateCacheKey(accessToken);

        if (!forceRefresh)
        {
            try
            {
                // Attempt to retrieve the repository list from the distributed cache using the generated cache key.
                var cachedJson = await _cache.GetStringAsync(cacheKey, cancellationToken);
                if (!string.IsNullOrEmpty(cachedJson))
                {
                    var cachedRepos = JsonSerializer.Deserialize<List<GitHubRepositoryDto>>(cachedJson, JsonOptions);
                    if (cachedRepos != null)
                    {
                        _logger.LogInformation("[GitHubService] Successfully retrieved {Count} GitHub repositories from cache.", cachedRepos.Count);
                        return cachedRepos;
                    }
                }
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning("[GitHubService] Fetching repositories from cache was cancelled. Exception: {Message}", ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GitHubService] Failed to read or deserialize repositories from cache. Falling back to API call.");
            }
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
                _logger.LogError("[GitHubService] GitHub API returned error status code: {StatusCode} for getting repositories.", response.StatusCode);
                return [];
            }

            // Read and deserialize the response content into a list of GitHubRepositoryDto objects.
            var repositories = await response.Content.ReadFromJsonAsync<List<GitHubRepositoryDto>>(JsonOptions, cancellationToken) ?? [];

            try
            {
                // Cache the retrieved repository list in the distributed cache for 10 minutes.
                var cacheOptions = new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
                };
                var serializedRepos = JsonSerializer.Serialize(repositories, JsonOptions);

                // Store the serialized repository list in the distributed cache using the generated cache key and cache options.
                await _cache.SetStringAsync(cacheKey, serializedRepos, cacheOptions, cancellationToken);
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning("[GitHubService] Failed to save repositories to distributed cache. Exception: {Message}", ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GitHubService] Failed to save repositories to distributed cache.");
            }

            _logger.LogInformation("[GitHubService] Successfully retrieved and cached {Count} repositories from GitHub API.", repositories.Count);
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
}