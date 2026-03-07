namespace Core.Entities;


/// <summary>
/// Represents the configuration settings for a project, including the .NET version to use,
/// the port to expose, whether a database is required, and whether the project is public or private.
/// </summary>
public class ProjectConfiguration
{
    /// The unique identifier for the project configuration.
    public Guid Id { get; set; }

    /// The unique identifier of the project associated with this configuration.
    public Guid ProjectId { get; set; }

    /// The .NET version to use for the project, which is required for the application.
    public required string DotNetVersion { get; set; }

    /// The port to expose for the project, which can be null if no port is specified.
    public int? ExposedPort { get; set; }

    /// A boolean value to decide if the project needs a database.
    public bool RequiresDb { get; set; }

    /// A boolean value to decide if the project is to be published or just be run on localhost.
    public bool IsPublic { get; set; }

    /// A reference to the associated project, which can be null if the project is not loaded or has been deleted.
    public Project? Project { get; set; }
}