using Domain.DTO;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Application.Data.Users;

/// <summary>
///     Implementation of <see cref="IUserService" /> for managing users using Entity Framework
///     Core.
/// </summary>
/// <param name="dbContext">The database context used for data access.</param>
public sealed class UserService(AutoMateDbContext dbContext) : IUserService
{
    /// <summary>
    ///     Retrieves the internal system ID for a GitHub user based on their external GitHub
    ///     Account ID.
    /// </summary>
    /// <param name="githubAccountId">
    ///     The unique string account identifier provided by the GitHub OAuth API.
    /// </param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>
    ///     A task returning a <see cref="T:System.Guid" /> representing the internal user ID.
    ///     Returns <see cref="F:System.Guid.Empty" /> if the account ID is null/empty or the user
    ///     is not found.
    /// </returns>
    /// <exception cref="T:System.OperationCanceledException">
    ///     The underlying read or analysis observes cancellation.
    /// </exception>
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


    /// <summary>
    ///     Retrieves user details from a unique identifier, which can represent either a local
    ///     system user ID or a GitHub external account ID.
    /// </summary>
    /// <param name="identifier">
    ///     A string that uniquely identifies the user. This can be either a GUID (local user ID) or
    ///     a GitHub account ID.
    /// </param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>
    ///     A task returning a tuple containing the user ID (<see cref="T:System.Guid" />), an
    ///     optional access token (<see cref="T:System.String" />), and a flag indicating if the
    ///     user is a GitHub user (<see cref="T:System.Boolean" />). Returns (
    ///     <see
    ///         cref="F:System.Guid.Empty" />
    ///     , null, false) if the identifier is invalid or no matching
    ///     user is found.
    /// </returns>
    /// <exception cref="T:System.OperationCanceledException">
    ///     The underlying read or analysis observes cancellation.
    /// </exception>
    public async Task<(Guid UserId, string? AccessToken, bool IsGitHubUser)> GetUserDetailsFromIdentifierAsync(
        string identifier, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return (Guid.Empty, null, false);

        if (Guid.TryParse(identifier, out var localUserId))
            return await GetUserDetailsBySystemIdAsync(localUserId, cancellationToken);

        return await GetUserDetailsByGithubAccountIdAsync(identifier, cancellationToken);
    }


    /// <summary>
    ///     Determines whether the specified user has connected an Azure account.
    /// </summary>
    /// <param name="userId">The internal user ID.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>
    ///     A task returning true when Azure identity and token data are available; otherwise false.
    /// </returns>
    /// <exception cref="T:System.OperationCanceledException">
    ///     The underlying read or analysis observes cancellation.
    /// </exception>
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

    /// <summary>
    ///     Retrieves Azure cloud deployment credentials for the specified user.
    /// </summary>
    /// <param name="userId">The internal user ID.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>
    ///     A task returning the connected Azure credentials, or null when the user has not
    ///     connected Azure.
    /// </returns>
    /// <exception cref="T:System.OperationCanceledException">
    ///     The underlying read or analysis observes cancellation.
    /// </exception>
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
    /// <param name="userId">The internal AutoMate user identity.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning the grouped values described by this operation, preserving unavailable
    ///     fields.
    /// </returns>
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
    /// <param name="accountId">External provider account identity.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning the grouped values described by this operation, preserving unavailable
    ///     fields.
    /// </returns>
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
    /// <param name="userId">The internal AutoMate user identity.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning whether the documented local user exists async condition holds.</returns>
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
    /// <returns>The query remote users selection with its documented bounds and ordering.</returns>
    private IQueryable<RemoteUser> QueryRemoteUsers()
    {
        return dbContext.Users
            .OfType<RemoteUser>()
            .AsNoTracking();
    }

    /// <summary>
    ///     Minimal remote user projection used to avoid loading full entity graphs for identity
    ///     lookups.
    /// </summary>
    /// <param name="Id">The record identity.</param>
    /// <param name="AccountId">External provider account identity.</param>
    /// <param name="GitHubAccessToken">Provider credential; never include it in diagnostic output.</param>
    private sealed record RemoteUserDetails(Guid Id, string AccountId, string? GitHubAccessToken);
}
