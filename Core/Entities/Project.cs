using Core.Enums;

namespace Core.Entities;


/// <summary>
/// Represents a project created by a user, containing information
/// about the project's source code, type, and associated configuration and deployments.
/// </summary>
public class Project
{
    /// The unique identifier for the project.
    public Guid Id { get; set; }

    /// The unique identifier of the user who owns the project.
    public Guid UserId { get; set; }

    /// The name of the project, which is required for the application.
    public required string Name { get; set; }

    /// The type of source code for the project, represented by the SourceType enum.
    public required SourceType SourceType { get; set; }

    /// The path or URL to the project's source code, which is required for the application.
    public required string SourcePathOrUrl { get; set; }

    /// The type of application the project represents.
    public AppType AppType { get; set; }

    /// A reference to the user who owns the project.
    public User? User { get; set; }

    /// A reference to the project's configuration settings.
    public ProjectConfiguration? Configuration { get; set; }

    /// A collection of deployments associated with the project.
    public ICollection<Deployment> Deployments { get; set; } = [];
}