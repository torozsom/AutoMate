using Domain.DTO;

namespace Application.Data.Users;

/// <summary>
///     Defines operations related to user management and retrieval. Acts as an abstraction
///     layer between the UI and the data access layer.
/// </summary>
public interface IUserService
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
    ///     A task returning a <see cref="Guid" /> representing the internal user ID. Returns
    ///     <see
    ///         cref="Guid.Empty" />
    ///     if the account ID is null/empty or the user is not found.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    ///     The underlying read or analysis observes cancellation.
    /// </exception>
    Task<Guid> GetUserIdByGithubAccountIdAsync(string githubAccountId, CancellationToken cancellationToken = default);


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
    ///     A task returning a tuple containing the user ID (<see cref="Guid" />), an optional
    ///     access token (<see cref="string" />), and a flag indicating if the user is a GitHub user
    ///     (<see cref="bool" />). Returns (<see cref="Guid.Empty" />, null, false) if the
    ///     identifier is invalid or no matching user is found.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    ///     The underlying read or analysis observes cancellation.
    /// </exception>
    Task<(Guid UserId, string? AccessToken, bool IsGitHubUser)> GetUserDetailsFromIdentifierAsync(string identifier,
        CancellationToken cancellationToken = default);


    /// <summary>
    ///     Determines whether the specified user has connected an Azure account.
    /// </summary>
    /// <param name="userId">The internal user ID.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>
    ///     A task returning true when Azure identity and token data are available; otherwise false.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    ///     The underlying read or analysis observes cancellation.
    /// </exception>
    Task<bool> HasAzureConnectionAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Retrieves Azure cloud deployment credentials for the specified user.
    /// </summary>
    /// <param name="userId">The internal user ID.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>
    ///     A task returning the connected Azure credentials, or null when the user has not
    ///     connected Azure.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    ///     The underlying read or analysis observes cancellation.
    /// </exception>
    Task<AzureCloudCredentialsDto?> GetAzureCloudCredentialsAsync(Guid userId,
        CancellationToken cancellationToken = default);
}
