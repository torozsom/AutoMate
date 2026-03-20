using Core.DTO;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Services.GitHub;


/// <summary>
///     Service class responsible for interacting with the GitHub API
///     to retrieve user repositories and other related data.
/// </summary>
public class GitHubService : IGitHubService
{
    private readonly HttpClient _httpClient;

    /// <summary>
    ///     Initializes a new instance of the <see cref="GitHubService"/> class.
    ///     It configures the HttpClient with the base URL and sets the User-Agent header required by GitHub API.
    /// </summary>
    /// <param name="httpClient">The HttpClient instance used for making HTTP requests.</param>
    public GitHubService(HttpClient httpClient)
    {
        _httpClient = httpClient;

        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AutoMate", "1.0"));
        _httpClient.BaseAddress = new Uri("https://api.github.com/");
    }


    /// <summary>
    ///     Retrieves the list of repositories for the authenticated user using the provided access token.
    /// </summary>
    /// <param name="accessToken">The access token of the authenticated user.</param>
    /// <returns></returns>
    public async Task<List<GitHubRepositoryDto>> GetUserRepositoriesAsync(string accessToken)
    {
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _httpClient.GetAsync("user/repos?sort=updated&per_page=100");
        if (!response.IsSuccessStatusCode)
            return [];

        var repositories = await response.Content.ReadFromJsonAsync<List<GitHubRepositoryDto>>();
        return repositories ?? [];
    }

}
