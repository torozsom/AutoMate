using Core.DTO;
using Core.Entities;
using Core.Enums;
using Microsoft.EntityFrameworkCore;
using Services.Data;

namespace Services.Projects;

/// <summary>
///     Service class responsible for managing projects within the application.
///     It handles operations such as adding local and GitHub projects,
///     and interacting with the database to persist project information.
/// </summary>
/// <param name="context">The database context.</param>
public class ProjectService(AutoMateDbContext context) : IProjectService
{
    /// <summary>
    ///     Adds a local project to the database for a specific user. Checks if a project
    ///     with the same source path already exists for the user before adding.
    /// </summary>
    /// <param name="userId">The user ID of the project's owner.</param>
    /// <param name="project">The project information to be saved.</param>
    /// <param name="csproject">The C# project file information.</param>
    /// <returns>A task that returns true if the project was added successfully, or false if it already exists.</returns>
    public async Task<bool> AddLocalProjectAsync(Guid userId, LocalProjectDto project, CsProjectDto csproject)
    {
        // Check if a project with the same source path already exists for the user
        var proj = await context.Projects
            .Include(p => p.CsProjects)
            .FirstOrDefaultAsync(p => p.UserId == userId
                                      && p.SourceType == SourceType.Local
                                      && p.SourcePathOrUrl == project.Path);

        // If not, create a new project
        if (proj == null)
        {
            proj = new Project
            {
                UserId = userId,
                Name = project.Name,
                SourceType = SourceType.Local,
                SourcePathOrUrl = project.Path,
                CsProjects = []
            };
            context.Projects.Add(proj);
        }

        // Check if a C# project with the same path already exists for this project
        var csprojExists = proj.CsProjects.Any(csp => csp.Path == csproject.Path);
        if (csprojExists)
            return false;

        // If not, create a new C# project and add it to the project
        var csproj = new CsProject
        {
            ProjectId = proj.Id,
            Name = csproject.Name,
            Path = csproject.Path,
            IsWebProject = csproject.IsWebProject,
            Configuration = new LocalProjectConfig
            {
                DotNetVersion = "10.0",
                ExposedPort = 8080,
                RequiresDb = false,
                IsPublic = false
            }
        };

        proj.CsProjects.Add(csproj);
        await context.SaveChangesAsync();
        return true;
    }


    /// <summary>
    ///     Adds a GitHub project to the database for a specific user. Checks if a project
    ///     with the same source URL already exists for the user before adding.
    /// </summary>
    /// <param name="userId">The user ID of the project's owner.</param>
    /// <param name="projectName">The name of the project to be saved.</param>
    /// <param name="gitUrl">The git URL of the remote repository.</param>
    /// <returns>A task that returns true if the project was added successfully, or false if it already exists.</returns>
    public async Task<bool> AddGitHubProjectAsync(Guid userId, string projectName, string gitUrl)
    {
        // Check if a project with the same source URL already exists for the user
        var alreadyExists = await context.Projects
            .AnyAsync(p => p.UserId == userId && p.SourceType == SourceType.Remote && p.SourcePathOrUrl == gitUrl);

        if (alreadyExists)
            return false;

        // If not, create a new project
        var project = new Project
        {
            UserId = userId,
            Name = projectName,
            SourceType = SourceType.Remote,
            SourcePathOrUrl = gitUrl
        };

        context.Projects.Add(project);
        await context.SaveChangesAsync();
        return true;
    }


    /// <summary>
    ///     Retrieves a list of projects associated with a specific user. The method queries the database
    ///     for projects that match the provided user ID and returns them as a list. This allows users to
    ///     view all their projects in the application.
    /// </summary>
    /// <param name="userId"></param>
    /// <returns></returns>
    public async Task<List<Project>> GetProjectsAsync(Guid userId)
    {
        // Retrieves all projects for the specified user, including their associated C# projects
        return await context.Projects
            .Include(p => p.CsProjects)
            .Where(p => p.UserId == userId)
            .ToListAsync();
    }


    /// <summary>
    ///     Deletes a project associated with a specific user from the database.
    ///     Checks if the project exists before attempting to delete it.
    /// </summary>
    /// <param name="projectId">The unique identifier of the project to be deleted.</param>
    /// <param name="userId">The unique identifier of the user who owns the project.</param>
    /// <returns>A task that returns true if the project was deleted successfully, or false if the project does not exist.</returns>
    public async Task<bool> DeleteProjectAsync(Guid projectId, Guid userId)
    {
        // Check if the project exists before attempting to delete it
        var project = await context.Projects
            .FirstOrDefaultAsync(p => p.Id == projectId && p.UserId == userId);

        if (project == null)
            return false;

        // Delete the project and its associated C# projects
        context.Projects.Remove(project);
        await context.SaveChangesAsync();
        return true;
    }
}