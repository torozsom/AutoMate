using Core.Enums;

namespace Core.Entities;


/// <summary>
/// Represents a project created by a user, containing information
/// about the project's source code, type, and associated configuration and deployments.
/// </summary>
public class Project
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public required string Name { get; set; }

    public required SourceType SourceType { get; set; }

    public string? SourcePathOrUrl { get; set; }

    public AppType AppType { get; set; }

    public User? User { get; set; }

    public ProjectConfiguration? Configuration { get; set; }

    public ICollection<Deployment>? Deployment { get; set; }
}