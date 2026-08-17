using Core.Defaults;

namespace Core.DTO;

/// <summary>
///     Represents the configuration settings required for deploying a .NET project, including project details,
///     environment settings, database configuration, and custom environment variables.
/// </summary>
public class DeploymentConfigDto
{
    /// <summary>
    ///     A unique identifier for the project being deployed, used to associate
    ///     the deployment configuration with a specific project in the system.
    /// </summary>
    public Guid ProjectId { get; set; }

    /// <summary>
    ///     A unique identifier for the C# project associated with this deployment configuration, used to link
    ///     the deployment settings to the specific project being deployed in the system.
    /// </summary>
    public Guid CsProjectId { get; set; }

    /// <summary>
    ///     The name of the project being deployed, used for display purposes
    ///     and to generate Docker image tags and container names.
    /// </summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>
    ///     The name of the environment for which the project is being deployed (e.g., "Development", "Staging", "Production").
    ///     This setting can be used to apply environment-specific configurations and optimizations during deployment.
    /// </summary>
    public string EnvironmentName { get; set; } = DeploymentDefaults.DevelopmentEnvironmentName;

    /// <summary>
    ///     The port number that the deployed application will expose for incoming HTTP requests.
    /// </summary>
    public int ExposedPort { get; set; } = DeploymentDefaults.LocalExposedPort;

    /// <summary>
    ///     Indicates whether this configuration targets Azure cloud deployment instead of local Docker deployment.
    /// </summary>
    public bool IsCloudDeployment { get; set; }

    /// <summary>
    ///     The Azure region where cloud resources should be created.
    /// </summary>
    public string CloudAzureRegion { get; set; } = DeploymentDefaults.AzureRegion;

    /// <summary>
    ///     The Azure resource group that will contain the deployment resources.
    /// </summary>
    public string CloudResourceGroupName { get; set; } = string.Empty;

    /// <summary>
    ///     The Azure Container App name.
    /// </summary>
    public string CloudContainerAppName { get; set; } = string.Empty;

    /// <summary>
    ///     The container registry name used by generated cloud deployment assets.
    /// </summary>
    public string CloudRegistryName { get; set; } = string.Empty;

    /// <summary>
    ///     A list of databases required by the deployed application.
    /// </summary>
    public List<DatabaseConfigDto> Databases { get; set; } = [];

    /// <summary>
    ///     A dictionary of custom environment variables to be set for the deployed application. This allows users to specify
    ///     additional configuration settings or secrets that the application may require at runtime, and these variables will
    ///     be included in the deployment configuration and made available to the application when it runs.
    /// </summary>
    public Dictionary<string, string> CustomEnvVars { get; set; } = new();
}