namespace Application.Abstractions.GitHub;

/// <summary>Issues installation-scoped GitHub App credentials and verifies user repository access.</summary>
public interface IGitHubAppCredentials
{
    /// <summary>Checks the current user's write access and resolves the installed App for the repository.</summary>
    Task<(long InstallationId, long RepositoryId)> ResolveRepositoryAsync(
        string userAccessToken, string owner, string repository, CancellationToken cancellationToken);

    /// <summary>Mints a short-lived token for one installation when a worker needs GitHub access.</summary>
    Task<string> CreateInstallationTokenAsync(long installationId, CancellationToken cancellationToken);
}