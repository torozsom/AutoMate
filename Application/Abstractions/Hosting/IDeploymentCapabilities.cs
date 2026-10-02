namespace Application.Abstractions.Hosting;

/// <summary>
///     Describes deployment operations enabled for the running AutoMate instance.
///     Capabilities are enforced by the worker and orchestrators, not only by the UI.
/// </summary>
public interface IDeploymentCapabilities
{
    /// <summary>Gets whether deployment from the host file system to its Docker daemon is allowed.</summary>
    bool LocalDeploymentsEnabled { get; }

    /// <summary>Gets whether GitHub to Azure Container Apps deployments are allowed.</summary>
    bool CloudDeploymentsEnabled { get; }
}