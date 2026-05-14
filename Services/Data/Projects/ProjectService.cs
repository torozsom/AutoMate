using Core.DTO;
using Core.Entities;
using Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Services.Data.Projects;

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


    /// <inheritdoc />
    public async Task<bool> AddLocalProjectAsync(Guid userId, LocalProjectDto project, CsProjectDto csproject,
        CancellationToken cancellationToken = default)
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
                                          && p.SourcePathOrUrl == project.Path, cancellationToken);

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

            await context.SaveChangesAsync(cancellationToken);

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


    /// <inheritdoc />
    public async Task<bool> AddGitHubProjectAsync(Guid userId, string projectName, string gitUrl,
        CancellationToken cancellationToken = default)
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
                .AnyAsync(p => p.UserId == userId && p.SourceType == SourceType.Remote && p.SourcePathOrUrl == gitUrl,
                    cancellationToken);

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
            await context.SaveChangesAsync(cancellationToken);

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


    /// <inheritdoc />
    public async Task<List<Project>> GetUserProjectsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await context.Projects
            .AsNoTracking()
            .Include(p => p.CsProjects)
            .ThenInclude(c => c.Deployments)
            .Where(p => p.UserId == userId)
            .ToListAsync(cancellationToken);
    }


    /// <inheritdoc />
    public async Task<bool> DeleteProjectAsync(Guid projectId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Check if the project exists before attempting to delete it
            var project = await context.Projects
                .FirstOrDefaultAsync(p => p.Id == projectId && p.UserId == userId, cancellationToken);

            if (project == null)
            {
                logger.LogWarning(
                    "[ProjectService] Project with ID {ProjectId} not found or does not belong to user {UserId}.",
                    projectId, userId);
                return false;
            }

            // Delete the project and its associated C# projects
            context.Projects.Remove(project);
            await context.SaveChangesAsync(cancellationToken);

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


    /// <inheritdoc />
    public async Task<Project?> GetProjectByIdAsync(Guid projectId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await context.Projects
            .Include(p => p.CsProjects)
            .ThenInclude(c => c.Deployments)
            .FirstOrDefaultAsync(p => p.Id == projectId && p.UserId == userId, cancellationToken);
    }


    /// <summary>
    ///     Creates a default C# project with predefined configuration settings.
    /// </summary>
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