namespace Application.Abstractions.GitHub;

/// <summary>
///     Issues installation-scoped GitHub App credentials and verifies user repository access.
/// </summary>
public interface IGitHubAppCredentials
{
    /// <summary>
    ///     Checks the current user's write access and resolves the installed App for the
    ///     repository.
    /// </summary>
    /// <param name="userAccessToken">The initiating user token used to verify repository access.</param>
    /// <param name="owner">The GitHub repository owner or organization.</param>
    /// <param name="repository">The GitHub repository name.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning the verified installation and repository identities authorized by the
    ///     initiating user.
    /// </returns>
    Task<(long InstallationId, long RepositoryId)> ResolveRepositoryAsync(
        string userAccessToken, string owner, string repository, CancellationToken cancellationToken);

    /// <summary>
    ///     Mints a short-lived token for one installation when a worker needs GitHub access.
    /// </summary>
    /// <param name="installationId">The verified GitHub App installation identity.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning a short-lived installation access token; never persist it in diagnostic
    ///     history.
    /// </returns>
    Task<string> CreateInstallationTokenAsync(long installationId, CancellationToken cancellationToken);
}
