using Core.Defaults;

namespace Core.DTO;

/// <summary>
///     Represents database settings rendered into generated deployment templates.
/// </summary>
public record DatabaseConfigDto
{
    /// <summary>
    ///     The database provider type, such as PostgreSql, SqlServer, or MySql.
    /// </summary>
    public string DbType { get; set; } = string.Empty;

    /// <summary>
    ///     The generated database name used by local Docker templates.
    /// </summary>
    public string DbName { get; set; } = DeploymentDefaults.DatabaseName;

    /// <summary>
    ///     The generated database username used by local Docker templates.
    /// </summary>
    public string DbUser { get; set; } = DeploymentDefaults.DatabaseUser;

    /// <summary>
    ///     The generated development/template password for local Docker assets; callers should not treat it as a secret.
    /// </summary>
    public string DbPassword { get; set; } = DeploymentDefaults.DatabasePassword;

    /// <summary>
    ///     The application configuration key where the generated connection string should be bound.
    /// </summary>
    public string ConnectionStringName { get; set; } = DeploymentDefaults.ConnectionStringName;

    /// <summary>
    ///     The suffix applied to generated database container names.
    /// </summary>
    public string ContainerNameSuffix { get; set; } = DeploymentDefaults.DatabaseContainerNameSuffix;
}