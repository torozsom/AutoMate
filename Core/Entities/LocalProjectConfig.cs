namespace Core.Entities;

/// <summary>
///     Represents the configuration settings for a project, including the .NET version to use,
///     the port to expose, whether a database is required, and whether the project is public or private.
/// </summary>
public class LocalProjectConfig
{
    /// <summary>
    ///     Gets or sets the unique identifier for the project configuration.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    ///     Gets or sets the unique identifier of the project associated with this configuration.
    /// </summary>
    public Guid CsProjectId { get; set; }

    /// <summary>
    ///     Gets or sets the .NET version to use for the project (e.g., "net8.0").
    /// </summary>
    public required string DotNetVersion { get; set; }

    /// <summary>
    ///     Gets or sets the port number to expose for the project.
    ///     A null value indicates no specific port is configured.
    /// </summary>
    public int? ExposedPort { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether the project requires a database.
    /// </summary>
    public bool RequiresDb { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether the project is publicly accessible
    ///     or restricted to localhost.
    /// </summary>
    public bool IsPublic { get; set; }

    /// <summary>
    ///     Gets or sets environment variables for the Docker container in JSON format.
    ///     Useful for passing connection strings or API keys securely.
    /// </summary>
    public string? EnvironmentVariablesJson { get; set; }

    /// <summary>
    ///     Gets or sets a reference to the project associated with this configuration.
    /// </summary>
    public CsProject? CsProject { get; set; }
}