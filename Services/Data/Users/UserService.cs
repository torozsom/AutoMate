using Core.DTO;
using Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Services.Data.Users;

/// <summary>
///     Implementation of <see cref="IUserService" /> for managing users using Entity Framework Core.
/// </summary>
/// <param name="dbContext">The database context used for data access.</param>
public sealed class UserService(AutoMateDbContext dbContext) : IUserService
{
    /// <inheritdoc />
    public async Task<Guid> GetUserIdByGithubAccountIdAsync(string githubAccountId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(githubAccountId)) return Guid.Empty;

        return await dbContext.Users
            .OfType<RemoteUser>()
            .AsNoTracking()
            .Where(u => u.AccountId == githubAccountId)
            .Select(u => u.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }


    /// <inheritdoc />
    public async Task<(Guid UserId, string? AccessToken, bool IsGitHubUser)> GetUserDetailsFromIdentifierAsync(
        string identifier, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return (Guid.Empty, null, false);

        if (Guid.TryParse(identifier, out var localUserId))
            return await GetUserDetailsBySystemIdAsync(localUserId, cancellationToken);

        return await GetUserDetailsByGithubAccountIdAsync(identifier, cancellationToken);
    }


    /// <inheritdoc />
    public async Task<bool> HasAzureConnectionAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            return false;

        return await dbContext.Users
            .OfType<RemoteUser>()
            .AsNoTracking()
            .AnyAsync(u => u.Id == userId
                           && !string.IsNullOrWhiteSpace(u.AzureAccountId)
                           && !string.IsNullOrWhiteSpace(u.AzureTenantId)
                           && !string.IsNullOrWhiteSpace(u.AzureAccessToken),
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AzureCloudCredentialsDto?> GetAzureCloudCredentialsAsync(Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            return null;

        var credentials = await dbContext.Users
            .OfType<RemoteUser>()
            .AsNoTracking()
            .Where(u => u.Id == userId
                        && !string.IsNullOrWhiteSpace(u.AzureTenantId)
                        && !string.IsNullOrWhiteSpace(u.AzureSubscriptionId)
                        && !string.IsNullOrWhiteSpace(u.AzureAccessToken))
            .Select(u => new AzureCloudCredentialsDto
            {
                TenantId = u.AzureTenantId!,
                SubscriptionId = u.AzureSubscriptionId!,
                AccessToken = u.AzureAccessToken!
            })
            .FirstOrDefaultAsync(cancellationToken);

        return credentials;
    }

    /// <summary>
    ///     Resolves user details when the caller supplies AutoMate's persisted user ID.
    /// </summary>
    private async Task<(Guid UserId, string? AccessToken, bool IsGitHubUser)> GetUserDetailsBySystemIdAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        var remoteUser = await QueryRemoteUsers()
            .Where(u => u.Id == userId)
            .Select(u => new RemoteUserDetails(u.Id, u.AccountId, u.GitHubAccessToken))
            .FirstOrDefaultAsync(cancellationToken);

        if (remoteUser is not null)
            return (remoteUser.Id, remoteUser.GitHubAccessToken, true);

        return await LocalUserExistsAsync(userId, cancellationToken)
            ? (userId, null, false)
            : (Guid.Empty, null, false);
    }

    /// <summary>
    ///     Resolves remote user details when the caller supplies the GitHub account ID claim.
    /// </summary>
    private async Task<(Guid UserId, string? AccessToken, bool IsGitHubUser)> GetUserDetailsByGithubAccountIdAsync(
        string accountId, CancellationToken cancellationToken)
    {
        var githubUser = await QueryRemoteUsers()
            .Where(u => u.AccountId == accountId)
            .Select(u => new RemoteUserDetails(u.Id, u.AccountId, u.GitHubAccessToken))
            .FirstOrDefaultAsync(cancellationToken);

        return githubUser is not null
            ? (githubUser.Id, githubUser.GitHubAccessToken, true)
            : (Guid.Empty, null, false);
    }

    /// <summary>
    ///     Checks whether a persisted local user exists for the supplied AutoMate user ID.
    /// </summary>
    private async Task<bool> LocalUserExistsAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await dbContext.Users
            .OfType<LocalUser>()
            .AsNoTracking()
            .AnyAsync(u => u.Id == userId, cancellationToken);
    }

    /// <summary>
    ///     Builds the common no-tracking remote-user query used before projecting lookup results.
    /// </summary>
    private IQueryable<RemoteUser> QueryRemoteUsers()
    {
        return dbContext.Users
            .OfType<RemoteUser>()
            .AsNoTracking();
    }

    /// <summary>
    ///     Minimal remote user projection used to avoid loading full entity graphs for identity lookups.
    /// </summary>
    private sealed record RemoteUserDetails(Guid Id, string AccountId, string? GitHubAccessToken);
}