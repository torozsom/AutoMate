using Core.Defaults;
using Core.DTO;
using Core.Entities;

namespace Web.Components.Pages;

/// <summary>
///     Creates UI-layer default values used when deploying saved GitHub repositories without a local project scan.
/// </summary>
internal static class CloudDeploymentPageDefaults
{
    /// <summary>
    ///     Creates the default deployment configuration for a saved remote repository.
    /// </summary>
    /// <param name="app">The saved AutoMate application representing the remote repository.</param>
    /// <returns>A deployment configuration with the same defaults used by the dashboard and project detail pages.</returns>
    internal static DeploymentConfigDto CreateConfiguration(Application app)
    {
        var resourceName = ToAzureResourceName(app.Name);

        return new DeploymentConfigDto
        {
            ProjectId = app.Id,
            CsProjectId = Guid.Empty,
            ProjectName = app.Name,
            EnvironmentName = DeploymentDefaults.ProductionEnvironmentName,
            IsCloudDeployment = true,
            CloudAzureRegion = DeploymentDefaults.AzureRegion,
            CloudResourceGroupName = $"{resourceName}-prod-rg",
            CloudContainerAppName = $"{resourceName}-prod-app",
            CloudRegistryName = "ghcr.io",
            Databases = []
        };
    }


    /// <summary>
    ///     Creates metadata for a remote repository deployment that has no local checkout to scan.
    /// </summary>
    /// <returns>The minimal metadata model expected by cloud deployment orchestration.</returns>
    internal static ProjectMetadataDto CreateRemoteProjectMetadata()
    {
        return new ProjectMetadataDto
        {
            TargetFramework = "net10.0",
            DotNetVersion = "10.0",
            IsWebProject = true
        };
    }


    /// <summary>
    ///     Normalizes a project name into the Azure resource-name format used by page defaults.
    /// </summary>
    /// <param name="value">The project name to normalize.</param>
    /// <returns>An Azure-friendly resource name with the existing AutoMate fallback and length limit.</returns>
    private static string ToAzureResourceName(string value)
    {
        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray());

        normalized = string.Join('-', normalized
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (string.IsNullOrWhiteSpace(normalized))
            normalized = "automate-app";

        return normalized.Length <= 32 ? normalized : normalized[..32].TrimEnd('-');
    }
}