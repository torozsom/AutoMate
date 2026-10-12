using Domain.Defaults;

namespace Domain.DTO;

/// <summary>
///     Represents database settings rendered into generated deployment templates.
/// </summary>
public record DatabaseConfigDto
{
    /// <summary>The database provider type, such as PostgreSql, SqlServer, or MySql.</summary>
    public string DbType { get; set; } = string.Empty;

    /// <summary>Database name supplied to the selected deployment templates.</summary>
    public string DbName { get; set; } = DeploymentDefaults.DatabaseName;

    /// <summary>Database user name supplied to the selected deployment templates.</summary>
    public string DbUser { get; set; } = DeploymentDefaults.DatabaseUser;

    /// <summary>Database password supplied to templates; configured values must be treated as secrets.</summary>
    public string DbPassword { get; set; } = DeploymentDefaults.DatabasePassword;

    /// <summary>
    ///     The application configuration key where the generated connection string should be bound.
    /// </summary>
    public string ConnectionStringName { get; set; } = DeploymentDefaults.ConnectionStringName;

    /// <summary>The suffix applied to generated database container names.</summary>
    public string ContainerNameSuffix { get; set; } = DeploymentDefaults.DatabaseContainerNameSuffix;
}