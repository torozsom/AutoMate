namespace Domain.Entities;

/// <summary>Immutable, non-secret settings captured before a deployment starts.</summary>
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

/// <summary>Non-secret database composition; users, passwords and connection strings are deliberately excluded.</summary>
public sealed record DeploymentDatabaseSnapshot(string Provider, string Name, string ContainerSuffix);