namespace Application.Abstractions.Hosting;

/// <summary>
///     Immutable capability set selected by the host's deployment profile.
/// </summary>
public sealed record DeploymentCapabilities(bool LocalDeploymentsEnabled, bool CloudDeploymentsEnabled)
    : IDeploymentCapabilities;
