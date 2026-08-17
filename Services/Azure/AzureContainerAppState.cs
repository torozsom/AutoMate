namespace Services.Azure;

/// <summary>
///     Runtime availability state read from an Azure Container App.
/// </summary>
/// <param name="LatestRevision">The latest ready revision name reported by Azure.</param>
/// <param name="Fqdn">The public ingress FQDN when the app exposes one.</param>
internal sealed record AzureContainerAppState(string LatestRevision, string Fqdn);