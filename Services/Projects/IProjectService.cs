using Core.DTO;
using Core.Entities;

namespace Services.Projects;

/// <summary>
///     Service interface for managing projects. Provides methods for adding, retrieving, updating, and deleting projects.
/// </summary>
public interface IProjectService
{
    /// <summary>
    ///     Adds a project from a local file path.
    /// </summary>
    /// <param name="userId">The unique identifier of the user adding the project.</param>
    /// <param name="project">The project information.</param>
    /// <param name="csproj">The C# project file information.</param>
    /// <returns>
    ///     A task that represents the asynchronous operation. The task result is true if the project was added
    ///     successfully; otherwise, false.
    /// </returns>
    Task<bool> AddLocalProjectAsync(Guid userId, LocalProjectDto project, CsProjectDto csproject);

    /// <summary>
    ///     Adds a project from a GitHub repository URL.
    /// </summary>
    /// <param name="userId">The unique identifier of the user adding the project.</param>
    /// <param name="projectName">The name of the project.</param>
    /// <param name="gitUrl">The URL of the GitHub repository.</param>
    /// <returns>
    ///     A task that represents the asynchronous operation. The task result is true if the project was added
    ///     successfully; otherwise, false.
    /// </returns>
    Task<bool> AddGitHubProjectAsync(Guid userId, string projectName, string gitUrl);

    /// <summary>
    ///     Retrieves a list of projects associated with a specific user.
    /// </summary>
    /// <param name="userId">The unique identifier of the user getting their projects.</param>
    /// <returns></returns>
    Task<List<Project>> GetProjectsAsync(Guid userId);


    /// <summary>
    ///     Deletes a specific project associated with a user.
    /// </summary>
    /// <param name="projectId">The unique identifier of the project to be deleted.</param>
    /// <param name="userId">The unique identifier of the user attempting to delete the project.</param>
    /// <returns>
    ///     A task that represents the asynchronous operation. The task result is true if the project was deleted
    ///     successfully; otherwise, false.
    /// </returns>
    Task<bool> DeleteProjectAsync(Guid projectId, Guid userId);
}