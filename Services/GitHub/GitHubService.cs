using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Core.DTO;
using Microsoft.Extensions.Caching.Distributed;

namespace Services.GitHub;

/// <summary>
///     Service class responsible for interacting with the GitHub API
///     to retrieve user repositories and other related data.
/// </summary>
public class GitHubService : IGitHubService
{
    private readonly IDistributedCache _cache;
    private readonly HttpClient _httpClient;


    /// <summary>
    ///     Initializes a new instance of the <see cref="GitHubService" /> class.
    ///     It configures the HttpClient with the base URL and sets the User-Agent header required by GitHub API.
    /// </summary>
    /// <param name="httpClient">The HttpClient instance used for making HTTP requests.</param>
    /// <param name="cache">The IMemoryCache instance used for caching data.</param>
    public GitHubService(HttpClient httpClient, IDistributedCache cache)
    {
        _httpClient = httpClient;
        _cache = cache;

        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AutoMate", "1.0"));
        _httpClient.BaseAddress = new Uri("https://api.github.com/");
    }


    /// <summary>
    ///     Retrieves the list of repositories for the authenticated user using the provided access token.
    /// </summary>
    /// <param name="accessToken">The access token of the authenticated user.</param>
    /// <param name="forceRefresh">A flag indicating whether to force a refresh of the repository list.</param>
    /// <returns>A list of GitHubRepositoryDto objects representing the user's repositories.</returns>
    public async Task<List<GitHubRepositoryDto>> GetUserRepositoriesAsync(string accessToken, bool forceRefresh)
    {
        // Create a unique cache key based on the access token to store the user's repositories
        var cacheKey = $"github_repos_{accessToken}";

        // Check if the repositories are already cached and not expired
        if (!forceRefresh)
        {
            var cachedJson = await _cache.GetStringAsync(cacheKey);
            if (!string.IsNullOrEmpty(cachedJson))
            {
                var cachedRepos = JsonSerializer.Deserialize<List<GitHubRepositoryDto>>(cachedJson);
                if (cachedRepos != null)
                    return cachedRepos;
            }
        }

        // Set the Authorization header with the Bearer token for authentication
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // Make the API request to retrieve the user's repositories
        var response = await _httpClient.GetAsync("user/repos?sort=updated&per_page=100");
        if (!response.IsSuccessStatusCode)
            return [];

        var repositories = await response.Content.ReadFromJsonAsync<List<GitHubRepositoryDto>>() ?? [];

        // Cache the repositories for 10 minutes
        var cacheOptions = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
        };
        var serializedRepos = JsonSerializer.Serialize(repositories);
        await _cache.SetStringAsync(cacheKey, serializedRepos, cacheOptions);

        return repositories;
    }
}