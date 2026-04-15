using Core.Enums;

namespace Core.Entities;

/// <summary>
///     Represents a project created by a user, containing information
///     about the project's source code, type, and associated configuration and deployments.
/// </summary>
public class Project
{
    /// <summary>
    ///     Gets or sets the unique identifier for the project.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    ///     Gets or sets the unique identifier of the user who owns the project.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    ///     Gets or sets the name of the project. This is a required field.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    ///     Gets or sets the source type (e.g., Local or Remote) for the project.
    /// </summary>
    public required SourceType SourceType { get; set; }

    /// <summary>
    ///     Gets or sets the path (for local) or URL (for remote) to the project's source code.
    /// </summary>
    public required string SourcePathOrUrl { get; set; }

    /// <summary>
    ///     Gets or sets the type of the application (e.g., WebApi, Blazor, Mvc).
    /// </summary>
    public AppType AppType { get; set; }

    /// <summary>
    ///     Gets or sets the user who owns the project.
    /// </summary>
    public User? User { get; set; }

    /// <summary>
    ///     Gets or sets the collection of C# projects in the application.
    /// </summary>
    public ICollection<CsProject> CsProjects { get; set; } = [];
}