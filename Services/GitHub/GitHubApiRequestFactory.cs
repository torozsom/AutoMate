using System.Net.Http.Headers;

namespace Services.GitHub;

/// <summary>
///     Creates authenticated GitHub REST API requests with consistent headers and URI escaping.
/// </summary>
internal static class GitHubApiRequestFactory
{
    /// <summary>
    ///     GitHub REST API version sent with mutating and workflow requests.
    /// </summary>
    private const string ApiVersion = "2022-11-28";

    /// <summary>
    ///     Creates a request for a GitHub API endpoint relative to the configured base address.
    /// </summary>
    public static HttpRequestMessage Create(string accessToken, HttpMethod method, string requestUri)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("GitHub access token is required.", nameof(accessToken));

        var request = new HttpRequestMessage(method, EscapeGitHubRequestUri(requestUri));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        return request;
    }

    /// <summary>
    ///     Escapes path segments while preserving query parameters that are already encoded by callers.
    /// </summary>
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
}