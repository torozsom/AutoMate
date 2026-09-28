namespace Domain.DTO;

/// <summary>
///     Describes one Azure Container Apps deployment to monitor after its workflow succeeds.
/// </summary>
public sealed record AzureContainerAppRuntimeStreamRequest
{
    /// <summary>AutoMate project that owns the deployment.</summary>
    public Guid ProjectId { get; init; }

    /// <summary>Deployment receiving correlated runtime diagnostics.</summary>
    public Guid DeploymentId { get; init; }

    /// <summary>User whose encrypted Azure connection authorizes Monitor Logs queries.</summary>
    public Guid UserId { get; init; }

    /// <summary>Container App deployment configuration.</summary>
    public DeploymentConfigDto Config { get; init; } = new();

    /// <summary>ARM-scoped credentials used for state and metrics reads.</summary>
    public AzureCloudCredentialsDto AzureCredentials { get; init; } = new();
}
