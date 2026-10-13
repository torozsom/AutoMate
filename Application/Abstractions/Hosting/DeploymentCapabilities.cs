namespace Application.Abstractions.Hosting;

/// <summary>
///     Immutable capability set selected by the host's deployment profile.
/// </summary>
/// <param name="LocalDeploymentsEnabled">Whether this host permits local Docker operations.</param>
/// <param name="CloudDeploymentsEnabled">Whether this host permits cloud deployment operations.</param>
public sealed record DeploymentCapabilities(bool LocalDeploymentsEnabled, bool CloudDeploymentsEnabled)
    : IDeploymentCapabilities;
