using Core.DTO;

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
    /// <param name="projectName">The name of the project.</param>
    /// <param name="sourcePath">The file system path where the project is located.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is true if the project was added successfully; otherwise, false.</returns>
    Task<bool> AddLocalProjectAsync(Guid userId, string projectName, string sourcePath);

    /// <summary>
    ///     Adds a project from a GitHub repository URL.
    /// </summary>
    /// <param name="userId">The unique identifier of the user adding the project.</param>
    /// <param name="projectName">The name of the project.</param>
    /// <param name="gitUrl">The URL of the GitHub repository.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is true if the project was added successfully; otherwise, false.</returns>
    Task<bool> AddGitHubProjectAsync(Guid userId, string projectName, string gitUrl);
}