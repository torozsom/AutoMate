using Core.DTO;
using Core.Entities;
using Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Services.Data;

namespace Services.Projects;

/// <summary>
///     Service class responsible for managing projects within the application.
///     It handles operations such as adding local and GitHub projects,
///     and interacting with the database to persist project information.
/// </summary>
/// <param name="context">The database context.</param>
public class ProjectService(AutoMateDbContext context, ILogger<ProjectService> logger) : IProjectService
{
    private const string DefaultDotNetVersion = "10.0";
    private const int DefaultExposedPort = 8080;


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
        if (userId == Guid.Empty)
        {
            logger.LogWarning("[ProjectService] Attempted to add a local project with an empty user ID.");
            return false;
        }

        try
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
                logger.LogInformation("[ProjectService] Creating new local project '{ProjectName}' for user {UserId}.",
                    project.Name, userId);
            }

            // Check if a C# project with the same path already exists for this project
            var csprojExists = proj.CsProjects.Any(csp => csp.Path == csproject.Path);
            if (csprojExists)
            {
                logger.LogInformation(
                    "[ProjectService] C# project '{CsProjectName}' already exists in project '{ProjectName}'.",
                    csproject.Name, project.Name);
                return false;
            }

            // If not, create a new C# project and add it to the project
            var newCsProject = CreateDefaultCsProject(proj.Id, csproject);
            proj.CsProjects.Add(newCsProject);

            await context.SaveChangesAsync();
            logger.LogInformation(
                "[ProjectService] Successfully added C# project '{CsProjectName}' to project '{ProjectName}'.",
                csproject.Name, project.Name);

            return true;
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "[ProjectService] Database error occurred while adding local project '{ProjectName}'.",
                project.Name);
            return false;
        }
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
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(gitUrl))
        {
            logger.LogWarning("[ProjectService] Attempted to add a GitHub project with invalid parameters.");
            return false;
        }

        try
        {
            // Check if a project with the same source URL already exists for the user
            var alreadyExists = await context.Projects
                .AnyAsync(p => p.UserId == userId && p.SourceType == SourceType.Remote && p.SourcePathOrUrl == gitUrl);

            if (alreadyExists)
            {
                logger.LogInformation(
                    "[ProjectService] GitHub project with URL '{GitUrl}' already exists for user {UserId}.", gitUrl,
                    userId);
                return false;
            }

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

            logger.LogInformation(
                "[ProjectService] Successfully added GitHub project '{ProjectName}' for user {UserId}.", projectName,
                userId);
            return true;
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "[ProjectService] Database error occurred while adding GitHub project '{ProjectName}'.",
                projectName);
            return false;
        }
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
        return await context.Projects
            .AsNoTracking()
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
        try
        {
            // Check if the project exists before attempting to delete it
            var project = await context.Projects
                .FirstOrDefaultAsync(p => p.Id == projectId && p.UserId == userId);

            if (project == null)
            {
                logger.LogWarning(
                    "[ProjectService] Project with ID {ProjectId} not found or does not belong to user {UserId}.",
                    projectId, userId);
                return false;
            }

            // Delete the project and its associated C# projects
            context.Projects.Remove(project);
            await context.SaveChangesAsync();

            logger.LogInformation("[ProjectService] Successfully deleted project {ProjectId} for user {UserId}.",
                projectId, userId);
            return true;
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "[ProjectService] Database error occurred while deleting project {ProjectId}.",
                projectId);
            return false;
        }
    }


    /// <summary>
    ///     Creates a default C# project with predefined configuration settings.
    ///     This method is used when adding a new local project to ensure that the
    ///     C# project has consistent default values for properties such as .NET version,
    ///     exposed port, and database requirements.
    /// </summary>
    /// <param name="projectId">
    ///     The unique identifier of the parent project to which this C# project belongs.
    /// </param>
    /// <param name="dto">
    ///     The data transfer object containing the name, path, and web project status of the C# project.
    /// </param>
    /// <returns>
    ///     A new instance of the CsProject class initialized with the provided information and default configuration settings.
    /// </returns>
    private static CsProject CreateDefaultCsProject(Guid projectId, CsProjectDto dto)
    {
        return new CsProject
        {
            ProjectId = projectId,
            Name = dto.Name,
            Path = dto.Path,
            IsWebProject = dto.IsWebProject,
            Configuration = new LocalProjectConfig
            {
                DotNetVersion = DefaultDotNetVersion,
                ExposedPort = DefaultExposedPort,
                RequiresDb = false,
                IsPublic = false
            }
        };
    }
}