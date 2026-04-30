using Core.DTO;
using Core.Entities;

namespace Services.Projects;

/// <summary>
///     Service interface for managing projects. Provides methods for adding, retrieving, updating, and deleting projects.
/// </summary>
public interface IProjectService
{
    /// Adds a local project to the database for a specific user.
    Task<bool> AddLocalProjectAsync(Guid userId, LocalProjectDto project, CsProjectDto csproject);

    /// Adds a GitHub project to the database for a specific user.
    Task<bool> AddGitHubProjectAsync(Guid userId, string projectName, string gitUrl);

    /// Retrieves a list of projects associated with a specific user.
    Task<List<Project>> GetUserProjectsAsync(Guid userId);

    /// Deletes a specific project associated with a user.
    Task<bool> DeleteProjectAsync(Guid projectId, Guid userId);

    /// Retrieves a specific project by its ID for a given user.
    Task<Project?> GetProjectByIdAsync(Guid projectId, Guid userId);
}