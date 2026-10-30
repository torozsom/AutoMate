namespace Domain.Entities.Projects;

/// <summary>
///     Saved deployment settings and separate AI egress consent for one C# project.
/// </summary>
public class Configuration : BaseEntity
{
    /// <summary>The unique identifier of the C# project associated with this configuration.</summary>
    public Guid CsProjectId { get; init; }

    /// <summary>A reference to the C# project associated with this configuration.</summary>
    public CsProject? CsProject { get; init; }

    /// <summary>The .NET version to use for the project (e.g., "net8.0").</summary>
    public required string DotNetVersion { get; set; }

    /// <summary>
    ///     The port number to expose for the project. A null value indicates no specific port is
    ///     configured.
    /// </summary>
    public int? LocalExposedPort { get; init; }

    /// <summary>Whether the project requires a database.</summary>
    public bool RequiresDb { get; init; }

    /// <summary>
    ///     Requested network-exposure setting; not an authorization or resource-availability
    ///     guarantee.
    /// </summary>
    public bool IsPublic { get; init; }

    /// <summary>
    ///     Saved environment-variable JSON; values can include secrets and require protected
    ///     handling.
    /// </summary>
    public string? EnvironmentVariablesJson { get; init; }

    /// <summary>Owner-granted consent for redacted diagnostics to leave AutoMate for AI analysis.</summary>
    public bool AiDiagnosticEgressConsented { get; set; }


    /// <summary>The Azure region where the container app should be deployed (e.g., "eastus").</summary>
    public string? CloudAzureRegion { get; init; }

    /// <summary>
    ///     The name of the Azure Container Registry (ACR) where the container image will be pushed.
    /// </summary>
    public string? CloudRegistryName { get; init; }

    /// <summary>The name of the resource group in Azure where the container app will be deployed.</summary>
    public string? CloudResourceGroupName { get; init; }

    /// <summary>The name of the container app in Azure.</summary>
    public string? CloudContainerAppName { get; init; }
}
