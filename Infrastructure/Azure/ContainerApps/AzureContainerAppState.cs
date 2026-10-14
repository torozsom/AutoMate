namespace Infrastructure.Azure.ContainerApps;

/// <summary>
///     Runtime availability state read from an Azure Container App.
/// </summary>
/// <param name="LatestRevision">The latest ready revision name reported by Azure.</param>
/// <param name="Fqdn">The public ingress FQDN when the app exposes one.</param>
/// <param name="Image">Resolved container image reference, if available.</param>
/// <param name="ManagedEnvironmentId">
///     ARM log scope for the environment that owns console and system records.
/// </param>
internal sealed record AzureContainerAppState(
    string LatestRevision,
    string Fqdn,
    string? Image = null,
    string? ManagedEnvironmentId = null);

/// <summary>
///     Verified query destination and ARM-derived environment names for workspace isolation.
/// </summary>
/// <param name="ResourceId">Verified Azure resource identity used to scope provider reads.</param>
/// <param name="EnvironmentName">Runtime environment name used for deployment assets.</param>
/// <param name="ResourceName">The bounded provider resource name used to identify observations.</param>
internal sealed record AzureContainerAppLogScope(string ResourceId, string? EnvironmentName, string? ResourceName);
