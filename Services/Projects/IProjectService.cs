using Core.DTO;

namespace Services.Projects;


/// <summary>
///     Service interface for managing projects. Provides methods for adding, retrieving, updating, and deleting projects.
/// </summary>
public interface IProjectService
{
    Task<bool> AddLocalProjectAsync(Guid userId, string projectName, string sourcePath);

}