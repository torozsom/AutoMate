using Domain.Defaults;

namespace Domain.DTO.Deployment;

/// <summary>
///     Represents the configuration settings required for deploying a .NET project, including
///     project details, environment settings, database configuration, and custom environment
///     variables.
/// </summary>
public class DeploymentConfigDto
{
    /// <summary>Identity of the application being deployed.</summary>
    public Guid ProjectId { get; init; }

    /// <summary>Identity of the selected C# project within the application.</summary>
    public Guid CsProjectId { get; set; }

    /// <summary>Display and artifact name used for the deployment.</summary>
    public string ProjectName { get; init; } = string.Empty;

    /// <summary>Runtime environment name used by generated deployment assets.</summary>
    public string EnvironmentName { get; set; } = DeploymentDefaults.DevelopmentEnvironmentName;

    /// <summary>The port number that the deployed application will expose for incoming HTTP requests.</summary>
    public int ExposedPort { get; set; } = DeploymentDefaults.LocalExposedPort;

    /// <summary>
    ///     Indicates whether this configuration targets Azure cloud deployment instead of local
    ///     Docker deployment.
    /// </summary>
    public bool IsCloudDeployment { get; set; }

    /// <summary>The Azure region where cloud resources should be created.</summary>
    public string CloudAzureRegion { get; set; } = DeploymentDefaults.AzureRegion;

    /// <summary>The Azure resource group that will contain the deployment resources.</summary>
    public string CloudResourceGroupName { get; set; } = string.Empty;

    /// <summary>The Azure Container App name.</summary>
    public string CloudContainerAppName { get; set; } = string.Empty;

    /// <summary>The container registry name used by generated cloud deployment assets.</summary>
    public string CloudRegistryName { get; set; } = string.Empty;

    /// <summary>A list of databases required by the deployed application.</summary>
    public List<DatabaseConfigDto> Databases { get; set; } = [];

    /// <summary>
    ///     Custom runtime environment values; the map can contain credentials and must not be
    ///     logged.
    /// </summary>
    public Dictionary<string, string> CustomEnvVars { get; set; } = new();
}
