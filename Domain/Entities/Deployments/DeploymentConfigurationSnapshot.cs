namespace Domain.Entities;

/// <summary>
///     Non-secret launch settings and resolved artifact metadata saved for one deployment.
/// </summary>
/// <param name="ProjectName">The project name recorded for this deployment.</param>
/// <param name="Provider">The deployment or database provider recorded in the snapshot.</param>
/// <param name="SourceUrl">
///     The recorded repository URL, or null for unavailable or local source metadata.
/// </param>
/// <param name="Branch">The recorded deployment branch, if known.</param>
/// <param name="Commit">The resolved deployment commit, if known.</param>
/// <param name="Environment">The recorded runtime environment name.</param>
/// <param name="Runtime">The recorded .NET runtime version, if known.</param>
/// <param name="Port">The recorded application port.</param>
/// <param name="Public">The requested exposure setting captured for this deployment.</param>
/// <param name="Region">The recorded cloud region, if applicable.</param>
/// <param name="ResourceGroup">The recorded cloud resource-group name, if applicable.</param>
/// <param name="ContainerApp">The recorded Azure Container App name, if applicable.</param>
/// <param name="Registry">The recorded container registry, if applicable.</param>
/// <param name="Image">The resolved image reference, if known.</param>
/// <param name="WorkflowFile">The recorded deployment workflow filename, if applicable.</param>
/// <param name="Databases">Non-secret database composition, or null for an older snapshot without it.</param>
public sealed record DeploymentConfigurationSnapshot(
    string ProjectName,
    string Provider,
    string? SourceUrl,
    string? Branch,
    string? Commit,
    string Environment,
    string? Runtime,
    int Port,
    bool Public,
    string? Region,
    string? ResourceGroup,
    string? ContainerApp,
    string? Registry,
    string? Image = null,
    string? WorkflowFile = null,
    IReadOnlyList<DeploymentDatabaseSnapshot>? Databases = null);

/// <summary>
///     Non-secret database composition; users, passwords and connection strings are
///     deliberately excluded.
/// </summary>
/// <param name="Provider">The deployment or database provider recorded in the snapshot.</param>
/// <param name="Name">The recorded database name.</param>
/// <param name="ContainerSuffix">The stable database container or component suffix.</param>
public sealed record DeploymentDatabaseSnapshot(string Provider, string Name, string ContainerSuffix);
