using Application.Abstractions.Hosting;

namespace Web.Configs;

/// <summary>
///     Selects which deployment capabilities are exposed by this AutoMate installation.
/// </summary>
public sealed class HostingProfileOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "HostingProfile";

    /// <summary>
    ///     Gets or sets the profile name. Supported values are <c>SelfHosted</c> and <c>SaaS</c>.
    /// </summary>
    public string Mode { get; init; } = "SelfHosted";

    /// <summary>Creates the server-enforced deployment capabilities for the selected profile.</summary>
    public IDeploymentCapabilities ToCapabilities()
    {
        return Mode.Trim().ToUpperInvariant() switch
        {
            "SELFHOSTED" => new DeploymentCapabilities(LocalDeploymentsEnabled: true, CloudDeploymentsEnabled: true),
            "SAAS" => new DeploymentCapabilities(LocalDeploymentsEnabled: false, CloudDeploymentsEnabled: true),
            _ => throw new InvalidOperationException(
                $"HostingProfile:Mode '{Mode}' is invalid. Use 'SelfHosted' or 'SaaS'.")
        };
    }
}
