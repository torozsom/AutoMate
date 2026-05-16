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

        var user = await dbContext.Users
            .OfType<GitHubUser>()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.AccountId == githubAccountId, cancellationToken);

        return user?.Id ?? Guid.Empty;
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
            .OfType<GitHubUser>()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.AccountId == identifier, cancellationToken);

        return githubUser is not null
            ? (githubUser.Id, githubUser.AccessToken, true)
            : (Guid.Empty, null, false);
    }
}