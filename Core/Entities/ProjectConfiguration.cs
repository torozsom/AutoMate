namespace Core.Entities;


/// <summary>
/// Represents the configuration settings for a project, including the .NET version to use,
/// the port to expose, whether a database is required, and whether the project is public or private.
/// </summary>
public class ProjectConfiguration
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public required string DotNetVersion { get; set; }

    public required int ExposedPort { get; set; }

    public bool RequiresDb { get; set; }

    public bool IsPublic { get; set; }

    public Project? Project { get; set; }
}