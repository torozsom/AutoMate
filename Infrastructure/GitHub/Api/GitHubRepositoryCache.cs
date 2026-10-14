using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.DTO.GitHub;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Infrastructure.GitHub.Api;

/// <summary>
///     Handles distributed cache reads and writes for authenticated GitHub repository lists.
/// </summary>
/// <param name="cache">Stores bounded non-secret repository cache projections.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
internal sealed class GitHubRepositoryCache(IDistributedCache cache, ILogger logger)
{
    /// <summary>Cache lifetime for repository lists fetched from GitHub.</summary>
    private static readonly TimeSpan RepositoryCacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     Attempts to read repositories from cache unless the caller requested a refresh.
    /// </summary>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="forceRefresh">Whether to bypass the cached repository result.</param>
    /// <param name="jsonOptions">Serialization settings shared by the versioned storage format.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning the try get async selection with its documented bounds and ordering, or
    ///     null when no value is available.
    /// </returns>
    public async Task<List<GitHubRepositoryDto>?> TryGetAsync(string accessToken, bool forceRefresh,
        JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
    {
        if (forceRefresh)
            return null;

        try
        {
            var cachedJson = await cache.GetStringAsync(GenerateCacheKey(accessToken), cancellationToken);
            if (string.IsNullOrEmpty(cachedJson))
                return null;

            var cachedRepos = JsonSerializer.Deserialize<List<GitHubRepositoryDto>>(cachedJson, jsonOptions);
            if (cachedRepos == null)
                return null;

            logger.LogInformation(
                "[GitHubService] Successfully retrieved {Count} GitHub repositories from cache.",
                cachedRepos.Count);
            return cachedRepos;
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(
                "Repository cache read canceled: {FailureType}.", ex.GetType().Name);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Repository cache read unavailable; falling back to API: {FailureType}.",
                ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    ///     Attempts to cache repositories without failing the GitHub API operation when Redis is
    ///     unavailable.
    /// </summary>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="repositories">Accessible repository projections stored in the bounded cache.</param>
    /// <param name="jsonOptions">Serialization settings shared by the versioned storage format.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    public async Task TrySetAsync(string accessToken, IReadOnlyCollection<GitHubRepositoryDto> repositories,
        JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
    {
        try
        {
            var cacheOptions = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = RepositoryCacheTtl
            };
            var serializedRepos = JsonSerializer.Serialize(repositories, jsonOptions);

            await cache.SetStringAsync(GenerateCacheKey(accessToken), serializedRepos, cacheOptions,
                cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(
                "Repository cache write canceled: {FailureType}.",
                ex.GetType().Name);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Repository cache write unavailable: {FailureType}.", ex.GetType().Name);
        }
    }

    /// <summary>
    ///     Generates a token-specific cache key without storing the token itself.
    /// </summary>
    /// <param name="token">Scoped provider or verification token; never include it in diagnostics.</param>
    /// <returns>The stable identity derived from the documented metadata or content.</returns>
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
