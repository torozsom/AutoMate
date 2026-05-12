using Core.DTO;
using Core.Entities;

namespace Services.Projects;

/// <summary>
///     Service interface for managing projects. Provides methods for adding, retrieving, updating, and deleting projects.
/// </summary>
public interface IProjectService
{
    /// <summary>
    ///     Adds a local project to the database for a specific user. Checks if a project
    ///     with the same source path already exists for the user before adding.
    /// </summary>
    /// <param name="userId">The user ID of the project's owner.</param>
    /// <param name="project">The project information to be saved.</param>
    /// <param name="csproject">The C# project file information.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A task that returns true if the project was added successfully, or false if it already exists.</returns>
    Task<bool> AddLocalProjectAsync(Guid userId, LocalProjectDto project, CsProjectDto csproject,
        CancellationToken cancellationToken = default);


    /// <summary>
    ///     Adds a GitHub project to the database for a specific user. Checks if a project
    ///     with the same source URL already exists for the user before adding.
    /// </summary>
    /// <param name="userId">The user ID of the project's owner.</param>
    /// <param name="projectName">The name of the project to be saved.</param>
    /// <param name="gitUrl">The git URL of the remote repository.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A task that returns true if the project was added successfully, or false if it already exists.</returns>
    Task<bool> AddGitHubProjectAsync(Guid userId, string projectName, string gitUrl,
        CancellationToken cancellationToken = default);


    /// <summary>
    ///     Retrieves a list of projects associated with a specific user. The method queries the database
    ///     for projects that match the provided user ID and returns them as a list.
    /// </summary>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A list of projects belonging to the user.</returns>
    Task<List<Project>> GetUserProjectsAsync(Guid userId, CancellationToken cancellationToken = default);


    /// <summary>
    ///     Deletes a project associated with a specific user from the database.
    ///     Checks if the project exists before attempting to delete it.
    /// </summary>
    /// <param name="projectId">The unique identifier of the project to be deleted.</param>
    /// <param name="userId">The unique identifier of the user who owns the project.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A task that returns true if the project was deleted successfully, or false if the project does not exist.</returns>
    Task<bool> DeleteProjectAsync(Guid projectId, Guid userId, CancellationToken cancellationToken = default);


    /// <summary>
    ///     Retrieves a specific project by its ID for a given user. If the project is found,
    ///     it includes the associated C# projects in the result.
    /// </summary>
    /// <param name="projectId">The unique identifier of the project to retrieve.</param>
    /// <param name="userId">The unique identifier of the user who owns the project.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A task that returns the project if found, or null if not found.</returns>
    Task<Project?> GetProjectByIdAsync(Guid projectId, Guid userId, CancellationToken cancellationToken = default);
}