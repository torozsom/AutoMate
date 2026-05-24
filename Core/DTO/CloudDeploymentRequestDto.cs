namespace Core.DTO;

/// <summary>
///     Contains the repository and project details required to generate and commit cloud deployment assets.
/// </summary>
public record CloudDeploymentRequestDto
{
    /// <summary>
    ///     The deployment configuration used to render cloud deployment templates.
    /// </summary>
    public DeploymentConfigDto Config { get; init; } = new();

    /// <summary>
    ///     The metadata extracted from the selected C# project.
    /// </summary>
    public ProjectMetadataDto Metadata { get; init; } = new();

    /// <summary>
    ///     The selected C# project name without the .csproj extension.
    /// </summary>
    public string CsProjectName { get; init; } = string.Empty;

    /// <summary>
    ///     The repository root path used as the relative-path anchor while rendering templates.
    /// </summary>
    public string RepositoryRoot { get; init; } = string.Empty;

    /// <summary>
    ///     The GitHub access token with permission to push repository contents.
    /// </summary>
    public string GitHubAccessToken { get; init; } = string.Empty;

    /// <summary>
    ///     The GitHub repository owner or organization name.
    /// </summary>
    public string RepositoryOwner { get; init; } = string.Empty;

    /// <summary>
    ///     The GitHub repository name.
    /// </summary>
    public string RepositoryName { get; init; } = string.Empty;

    /// <summary>
    ///     The branch where AutoMate should commit generated cloud deployment files.
    /// </summary>
    public string BranchName { get; init; } = "automate/azure-deployment";
}