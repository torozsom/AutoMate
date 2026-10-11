using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.DTO;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Infrastructure.GitHub;

/// <summary>
///     Handles distributed cache reads and writes for authenticated GitHub repository lists.
/// </summary>
internal sealed class GitHubRepositoryCache(IDistributedCache cache, ILogger logger)
{
    /// <summary>
    ///     Cache lifetime for repository lists fetched from GitHub.
    /// </summary>
    private static readonly TimeSpan RepositoryCacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     Attempts to read repositories from cache unless the caller requested a refresh.
    /// </summary>
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
    ///     Attempts to cache repositories without failing the GitHub API operation when Redis is unavailable.
    /// </summary>
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