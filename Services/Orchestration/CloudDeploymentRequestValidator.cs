using Core.DTO;

namespace Services.Orchestration;

/// <summary>
///     Validates required inputs for cloud deployment orchestration.
/// </summary>
internal static class CloudDeploymentRequestValidator
{
    /// <summary>
    ///     Throws when required repository or credential values are missing.
    /// </summary>
    public static void Validate(CloudDeploymentRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.RepositoryRoot))
            throw new ArgumentException("Repository root is required for cloud deployment template generation.",
                nameof(request));

        if (string.IsNullOrWhiteSpace(request.RepositoryOwner))
            throw new ArgumentException("Repository owner is required for cloud deployment.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.RepositoryName))
            throw new ArgumentException("Repository name is required for cloud deployment.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.GitHubAccessToken))
            throw new ArgumentException("GitHub access token is required for cloud deployment.", nameof(request));
    }
}