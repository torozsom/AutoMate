namespace Core.DTO;

/// <summary>
///     Represents the configuration settings required for deploying a .NET project, including project details,
///     environment settings, database configuration, and custom environment variables.
/// </summary>
public class DeploymentConfigDto
{
    /// <summary>
    ///     A unique identifier for the project being deployed, used to associate
    ///     the deployment configuration with a specific project in the system.
    /// </summary>
    public Guid ProjectId { get; set; }

    /// <summary>
    ///     A unique identifier for the C# project associated with this deployment configuration, used to link
    ///     the deployment settings to the specific project being deployed in the system.
    /// </summary>
    public Guid CsProjectId { get; set; }

    /// <summary>
    ///     The name of the project being deployed, used for display purposes
    ///     and to generate Docker image tags and container names.
    /// </summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>
    ///     The name of the environment for which the project is being deployed (e.g., "Development", "Staging", "Production").
    ///     This setting can be used to apply environment-specific configurations and optimizations during deployment.
    /// </summary>
    public string EnvironmentName { get; set; } = "Development";

    /// <summary>
    ///     The port number that the deployed application will expose for incoming HTTP requests.
    /// </summary>
    public int ExposedPort { get; set; } = 8080;

    /// <summary>
    ///     Indicates whether the deployed application requires a database connection.
    /// </summary>
    public bool RequiresDb { get; set; }

    /// <summary>
    ///     The type of database to be used for the deployed application (e.g., "PostgreSQL", "MySQL", "SQL Server").
    ///     This setting can be used to determine the appropriate database configuration and connection settings during
    ///     deployment.
    /// </summary>
    public string DbType { get; set; } = "PostgreSQL";

    /// <summary>
    ///     The name of the database to be used for the deployed application.
    ///     This setting is used to configure the database connection string and ensure
    ///     that the application can connect to the correct database instance during deployment.
    /// </summary>
    public string DbName { get; set; } = "appdb";

    /// <summary>
    ///     The username for authenticating with the database. This setting is used to configure the database connection
    ///     string and ensure that the application can authenticate successfully with the database during deployment.
    /// </summary>
    public string DbUser { get; set; } = "admin";

    /// <summary>
    ///     The password for authenticating with the database. This setting is used to configure the database connection string
    ///     and ensure that the application can authenticate successfully with the database during deployment.
    /// </summary>
    public string DbPassword { get; set; } = "P@ssw0rd123!";

    /// <summary>
    ///     A dictionary of custom environment variables to be set for the deployed application. This allows users to specify
    ///     additional configuration settings or secrets that the application may require at runtime, and these variables will
    ///     be included in the deployment configuration and made available to the application when it runs.
    /// </summary>
    public Dictionary<string, string> CustomEnvVars { get; set; } = new();
}