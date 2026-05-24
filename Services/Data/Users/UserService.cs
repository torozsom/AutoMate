using Core.DTO;
using Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Services.Data.Users;

/// <summary>
///     Implementation of <see cref="IUserService" /> for managing users using Entity Framework Core.
/// </summary>
/// <param name="dbContext">The database context used for data access.</param>
public class UserService(AutoMateDbContext dbContext) : IUserService
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
        {
            var exists = await dbContext.Users.AnyAsync(u => u.Id == localUserId, cancellationToken);
            return exists ? (localUserId, null, false) : (Guid.Empty, null, false);
        }

        var githubUser = await dbContext.Users
            .OfType<RemoteUser>()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.AccountId == identifier, cancellationToken);

        return githubUser is not null
            ? (githubUser.Id, AccessToken: githubUser.GitHubAccessToken, true)
            : (Guid.Empty, null, false);
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
}
