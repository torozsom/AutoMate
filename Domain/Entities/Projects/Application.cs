using Domain.Entities.Users;
using Domain.Enums.Projects;

namespace Domain.Entities.Projects;

/// <summary>
///     Represents a project created by a user, containing information about the project's
///     source code, type, and associated configuration and deployments.
/// </summary>
public class Application : BaseEntity
{
    /// <summary>
    ///     Legacy compatibility flag; runtime diagnostics are always collected for active
    ///     deployments.
    /// </summary>
    public bool RuntimeDiagnosticsEnabled { get; set; } = true;

    /// <summary>
    ///     Legacy compatibility flag; approved telemetry storage is automatic, independently of AI
    ///     consent.
    /// </summary>
    public bool ManagedTelemetryConsent { get; init; } = true;

    /// <summary>The unique identifier of the user who owns the project.</summary>
    public Guid UserId { get; init; }

    /// <summary>The user who owns the project.</summary>
    public User? User { get; init; }

    /// <summary>The name of the project. This is a required field.</summary>
    public required string Name { get; set; }

    /// <summary>The source type (e.g., Local or Remote) for the project.</summary>
    public required SourceType SourceType { get; init; }

    /// <summary>The path (for local) or URL (for remote) to the project's source code.</summary>
    public required string SourcePathOrUrl { get; init; }

    /// <summary>The type of the application (e.g., WebApi, Blazor, Mvc).</summary>
    public AppType AppType { get; init; }

    /// <summary>The collection of C# projects in the application.</summary>
    public ICollection<CsProject> CsProjects { get; init; } = [];
}
