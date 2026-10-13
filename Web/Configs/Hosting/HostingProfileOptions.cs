using Application.Abstractions.Hosting;

namespace Web.Configs;

/// <summary>
///     Selects which deployment capabilities are exposed by this AutoMate installation.
/// </summary>
public sealed class HostingProfileOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "HostingProfile";

    /// <summary>Gets or sets the profile name. Supported values are <c>SelfHosted</c> and <c>SaaS</c>.</summary>
    public string Mode { get; init; } = "SelfHosted";

    /// <summary>
    ///     Creates the server-enforced deployment capabilities for the selected profile.
    /// </summary>
    /// <returns>Server-enforced capability flags for the configured hosting profile.</returns>
    /// <exception cref="InvalidOperationException">Required host or OAuth configuration is missing.</exception>
    public IDeploymentCapabilities ToCapabilities()
    {
        return Mode.Trim().ToUpperInvariant() switch
        {
            "SELFHOSTED" => new DeploymentCapabilities(true, true),
            "SAAS" => new DeploymentCapabilities(false, true),
            _ => throw new InvalidOperationException(
                $"HostingProfile:Mode '{Mode}' is invalid. Use 'SelfHosted' or 'SaaS'.")
        };
    }
}
