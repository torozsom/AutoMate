using Core.DTO;
using Core.Entities;
using Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Services.Data.Apps;

/// <summary>
///     Service class responsible for managing projects within the application.
///     It handles operations such as adding local and GitHub projects,
///     and interacting with the database to persist project information.
/// </summary>
/// <param name="context">The database context.</param>
public class ApplicationService(AutoMateDbContext context, ILogger<ApplicationService> logger) : IApplicationService
{
    private const string DefaultDotNetVersion = "10.0";
    private const int DefaultExposedPort = 8080;


    /// <inheritdoc />
    public async Task<bool> AddLocalAppAsync(Guid userId, LocalProjectDto project, CsProjectDto csproject,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            logger.LogWarning("[ApplicationService] Attempted to add a local project with an empty user ID.");
            return false;
        }

        var sourcePath = project.Path.Trim();
        var projectName = project.Name.Trim();
        var csProjectPath = csproject.Path.Trim();
        var csProjectName = csproject.Name.Trim();

        try
        {
            // Check if an app with the same source path already exists for the user
            var app = await context.Applications
                .Include(a => a.CsProjects)
                .FirstOrDefaultAsync(a => a.UserId == userId
                                          && a.SourceType == SourceType.Local
                                          && a.SourcePathOrUrl == sourcePath, cancellationToken);

            // If not, create a new project
            if (app == null)
            {
                app = new Application
                {
                    UserId = userId,
                    Name = projectName,
                    SourceType = SourceType.Local,
                    SourcePathOrUrl = sourcePath,
                    CsProjects = []
                };
                context.Applications.Add(app);
                logger.LogInformation(
                    "[ApplicationService] Creating new local project '{ProjectName}' for user {UserId}.",
                    projectName, userId);
            }

            // Check if a C# project with the same path already exists for this project
            var csprojExists = app.CsProjects.Any(csp => csp.Path == csProjectPath);
            if (csprojExists)
            {
                logger.LogInformation(
                    "[ApplicationService] C# project '{CsProjectName}' already exists in project '{ProjectName}'.",
                    csProjectName, projectName);
                return false;
            }

            // If not, create a new C# project and add it to the project
            var newCsProject = CreateDefaultCsProject(app.Id, csProjectName, csProjectPath, csproject.IsWebProject);
            app.CsProjects.Add(newCsProject);

            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "[ApplicationService] Successfully added C# project '{CsProjectName}' to project '{ProjectName}'.",
                csProjectName, projectName);
            return true;
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex,
                "[ApplicationService] Database error occurred while adding local project '{ProjectName}'.",
                projectName);
            return false;
        }
    }


    /// <inheritdoc />
    public async Task<bool> AddGitHubAppAsync(Guid userId, string appName, string gitUrl,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(gitUrl))
        {
            logger.LogWarning("[ApplicationService] Attempted to add a GitHub project with invalid parameters.");
            return false;
        }

        var normalizedGitUrl = gitUrl.Trim();
        var normalizedAppName = string.IsNullOrWhiteSpace(appName) ? normalizedGitUrl : appName.Trim();

        try
        {
            // Check if an app with the same source URL already exists for the user
            var alreadyExists = await context.Applications
                .AnyAsync(a => a.UserId == userId
                               && a.SourceType == SourceType.Remote
                               && a.SourcePathOrUrl == normalizedGitUrl, cancellationToken);

            if (alreadyExists)
            {
                logger.LogInformation(
                    "[ApplicationService] GitHub project with URL '{GitUrl}' already exists for user {UserId}.",
                    normalizedGitUrl, userId);
                return false;
            }

            // If not, create a new app
            var app = new Application
            {
                UserId = userId,
                Name = normalizedAppName,
                SourceType = SourceType.Remote,
                SourcePathOrUrl = normalizedGitUrl
            };

            context.Applications.Add(app);
            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "[ApplicationService] Successfully added GitHub project '{AppName}' for user {UserId}.",
                normalizedAppName,
                userId);
            return true;
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex,
                "[ApplicationService] Database error occurred while adding GitHub project '{AppName}'.",
                normalizedAppName);
            return false;
        }
    }


    /// <inheritdoc />
    public async Task<List<Application>> GetUserAppsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await context.Applications
            .AsNoTracking()
            .Include(a => a.CsProjects)
            .ThenInclude(csp => csp.Deployments)
            .Where(a => a.UserId == userId)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
    }


    /// <inheritdoc />
    public async Task<bool> DeleteAppAsync(Guid appId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Check if the app exists before attempting to delete it
            var app = await context.Applications
                .FirstOrDefaultAsync(a => a.Id == appId && a.UserId == userId, cancellationToken);

            if (app == null)
            {
                logger.LogWarning(
                    "[ApplicationService] Project with ID {AppId} not found or does not belong to user {UserId}.",
                    appId, userId);
                return false;
            }

            // Delete the app and its associated C# projects
            context.Applications.Remove(app);
            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation("[ApplicationService] Successfully deleted project {AppId} for user {UserId}.",
                appId, userId);
            return true;
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "[ApplicationService] Database error occurred while deleting project {AppId}.",
                appId);
            return false;
        }
    }


    /// <inheritdoc />
    public async Task<Application?> GetAppByIdAsync(Guid appId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await context.Applications
            .AsNoTracking()
            .Include(a => a.CsProjects)
            .ThenInclude(csp => csp.Deployments)
            .AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Id == appId && a.UserId == userId, cancellationToken);
    }


    /// <summary>
    ///     Creates a default C# project with predefined configuration settings.
    /// </summary>
    private static CsProject CreateDefaultCsProject(Guid projectId, string name, string path, bool isWebProject)
    {
        return new CsProject
        {
            AppId = projectId,
            Name = name,
            Path = path,
            IsWebProject = isWebProject,
            Configuration = new Configuration
            {
                DotNetVersion = DefaultDotNetVersion,
                LocalExposedPort = DefaultExposedPort,
                RequiresDb = false,
                IsPublic = false
            }
        };
    }
}
