namespace Core.Entities;

/// <summary>
///     Represents the configuration settings for an application, including the .NET version to use,
///     the port to expose, whether a database is required, and whether the project is public or private.
/// </summary>
public class Configuration : BaseEntity
{
    /// <summary>
    ///     Gets or sets the unique identifier of the C# project associated with this configuration.
    /// </summary>
    public Guid CsProjectId { get; set; }

    /// <summary>
    ///     Gets or sets a reference to the C# project associated with this configuration.
    /// </summary>
    public CsProject? CsProject { get; set; }

    /// <summary>
    ///     Gets or sets the .NET version to use for the project (e.g., "net8.0").
    /// </summary>
    public required string DotNetVersion { get; set; }

    /// <summary>
    ///     Gets or sets the port number to expose for the project.
    ///     A null value indicates no specific port is configured.
    /// </summary>
    public int? LocalExposedPort { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether the project requires a database.
    /// </summary>
    public bool RequiresDb { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether the project is publicly accessible
    ///     or restricted to localhost.
    /// </summary>
    public bool IsPublic { get; set; }

    /// <summary>
    ///     Gets or sets environment variables for the Docker container in JSON format.
    ///     Useful for passing connection strings or API keys securely.
    /// </summary>
    public string? EnvironmentVariablesJson { get; set; }


    /// <summary>
    ///     Gets or sets the Azure region where the container app should be deployed (e.g., "eastus").
    /// </summary>
    public string? CloudAzureRegion { get; set; }

    /// <summary>
    ///     Gets or sets the name of the Azure Container Registry (ACR) where the container image will be pushed.
    /// </summary>
    public string? CloudRegistryName { get; set; }

    /// <summary>
    ///     Gets or sets the name of the resource group in Azure where the container app will be deployed.
    /// </summary>
    public string? CloudResourceGroupName { get; set; }

    /// <summary>
    ///     Gets or sets the name of the container app in Azure.
    /// </summary>
    public string? CloudContainerAppName { get; set; }
}