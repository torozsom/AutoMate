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
public sealed class ApplicationService(AutoMateDbContext context, ILogger<ApplicationService> logger)
    : IApplicationService
{
    /// <summary>
    ///     Default .NET version assigned to newly discovered C# projects.
    /// </summary>
    private const string DefaultDotNetVersion = "10.0";

    /// <summary>
    ///     Default local port assigned to newly discovered C# web projects.
    /// </summary>
    private const int DefaultExposedPort = 8080;


    /// <inheritdoc />
    public async Task<bool> AddLocalAppAsync(Guid userId, LocalProjectDto project, CsProjectDto csproject,
        CancellationToken cancellationToken = default)
    {
        if (!TryCreateLocalAppInput(userId, project, csproject, out var input))
        {
            logger.LogWarning("[ApplicationService] Attempted to add a local project with an empty user ID.");
            return false;
        }

        try
        {
            var app = await GetLocalAppWithProjectsAsync(input, cancellationToken);

            if (app == null)
            {
                app = CreateLocalApplication(input);
                context.Applications.Add(app);
                logger.LogInformation(
                    "[ApplicationService] Creating new local project '{ProjectName}' for user {UserId}.",
                    input.ProjectName, input.UserId);
            }

            if (ContainsCsProject(app, input.CsProjectPath))
            {
                logger.LogInformation(
                    "[ApplicationService] C# project '{CsProjectName}' already exists in project '{ProjectName}'.",
                    input.CsProjectName, input.ProjectName);
                return false;
            }

            app.CsProjects.Add(CreateDefaultCsProject(app.Id, input.CsProjectName, input.CsProjectPath,
                input.IsWebProject));

            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "[ApplicationService] Successfully added C# project '{CsProjectName}' to project '{ProjectName}'.",
                input.CsProjectName, input.ProjectName);
            return true;
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex,
                "[ApplicationService] Database error occurred while adding local project '{ProjectName}'.",
                input.ProjectName);
            return false;
        }
    }


    /// <inheritdoc />
    public async Task<bool> AddGitHubAppAsync(Guid userId, string appName, string gitUrl,
        CancellationToken cancellationToken = default)
    {
        if (!TryCreateRemoteAppInput(userId, appName, gitUrl, out var input))
        {
            logger.LogWarning("[ApplicationService] Attempted to add a GitHub project with invalid parameters.");
            return false;
        }

        try
        {
            var alreadyExists = await RemoteAppExistsAsync(input, cancellationToken);

            if (alreadyExists)
            {
                logger.LogInformation(
                    "[ApplicationService] GitHub project with URL '{GitUrl}' already exists for user {UserId}.",
                    input.GitUrl, input.UserId);
                return false;
            }

            context.Applications.Add(CreateRemoteApplication(input));
            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "[ApplicationService] Successfully added GitHub project '{AppName}' for user {UserId}.",
                input.AppName,
                input.UserId);
            return true;
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex,
                "[ApplicationService] Database error occurred while adding GitHub project '{AppName}'.",
                input.AppName);
            return false;
        }
    }


    /// <inheritdoc />
    public async Task<List<Application>> GetUserAppsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await QueryApplicationsWithProjectDeployments()
            .Where(a => a.UserId == userId)
            .ToListAsync(cancellationToken);
    }


    /// <inheritdoc />
    public async Task<bool> DeleteAppAsync(Guid appId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var app = await context.Applications
                .FirstOrDefaultAsync(a => a.Id == appId && a.UserId == userId, cancellationToken);

            if (app == null)
            {
                logger.LogWarning(
                    "[ApplicationService] Project with ID {AppId} not found or does not belong to user {UserId}.",
                    appId, userId);
                return false;
            }

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
        return await QueryApplicationsWithProjectDeployments()
            .FirstOrDefaultAsync(a => a.Id == appId && a.UserId == userId, cancellationToken);
    }


    /// <summary>
    ///     Builds the reusable read query for applications with projects and deployment history.
    /// </summary>
    private IQueryable<Application> QueryApplicationsWithProjectDeployments()
    {
        return context.Applications
            .AsNoTracking()
            .Include(a => a.CsProjects)
            .ThenInclude(csp => csp.Deployments)
            .AsSingleQuery();
    }

    /// <summary>
    ///     Finds an existing local application and eagerly loads its C# projects for duplicate checks.
    /// </summary>
    private async Task<Application?> GetLocalAppWithProjectsAsync(LocalAppInput input,
        CancellationToken cancellationToken)
    {
        return await context.Applications
            .Include(a => a.CsProjects)
            .FirstOrDefaultAsync(a => a.UserId == input.UserId
                                      && a.SourceType == SourceType.Local
                                      && a.SourcePathOrUrl == input.SourcePath, cancellationToken);
    }

    /// <summary>
    ///     Checks whether a remote GitHub application already exists for the user and repository URL.
    /// </summary>
    private async Task<bool> RemoteAppExistsAsync(RemoteAppInput input, CancellationToken cancellationToken)
    {
        return await context.Applications
            .AnyAsync(a => a.UserId == input.UserId
                           && a.SourceType == SourceType.Remote
                           && a.SourcePathOrUrl == input.GitUrl, cancellationToken);
    }

    /// <summary>
    ///     Creates a local application aggregate for a folder-based source.
    /// </summary>
    private static Application CreateLocalApplication(LocalAppInput input)
    {
        return new Application
        {
            UserId = input.UserId,
            Name = input.ProjectName,
            SourceType = SourceType.Local,
            SourcePathOrUrl = input.SourcePath,
            CsProjects = []
        };
    }

    /// <summary>
    ///     Creates a remote application aggregate for a GitHub repository source.
    /// </summary>
    private static Application CreateRemoteApplication(RemoteAppInput input)
    {
        return new Application
        {
            UserId = input.UserId,
            Name = input.AppName,
            SourceType = SourceType.Remote,
            SourcePathOrUrl = input.GitUrl
        };
    }

    /// <summary>
    ///     Creates a C# project with the default deployment configuration expected by the UI.
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

    /// <summary>
    ///     Checks for an already tracked C# project by exact project-file path.
    /// </summary>
    private static bool ContainsCsProject(Application app, string csProjectPath)
    {
        return app.CsProjects.Any(csp => csp.Path == csProjectPath);
    }

    /// <summary>
    ///     Validates and normalizes local application input from discovery DTOs.
    /// </summary>
    private static bool TryCreateLocalAppInput(Guid userId, LocalProjectDto project, CsProjectDto csProject,
        out LocalAppInput input)
    {
        input = default;

        if (userId == Guid.Empty)
            return false;

        input = new LocalAppInput(
            userId,
            project.Name.Trim(),
            project.Path.Trim(),
            csProject.Name.Trim(),
            csProject.Path.Trim(),
            csProject.IsWebProject);

        return true;
    }

    /// <summary>
    ///     Validates and normalizes remote application input from the GitHub repository form.
    /// </summary>
    private static bool TryCreateRemoteAppInput(Guid userId, string appName, string gitUrl, out RemoteAppInput input)
    {
        input = default;

        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(gitUrl))
            return false;

        var normalizedGitUrl = gitUrl.Trim();
        var normalizedAppName = string.IsNullOrWhiteSpace(appName) ? normalizedGitUrl : appName.Trim();

        input = new RemoteAppInput(userId, normalizedAppName, normalizedGitUrl);
        return true;
    }

    /// <summary>
    ///     Normalized values required to create or extend a local application aggregate.
    /// </summary>
    private readonly record struct LocalAppInput(
        Guid UserId,
        string ProjectName,
        string SourcePath,
        string CsProjectName,
        string CsProjectPath,
        bool IsWebProject);

    /// <summary>
    ///     Normalized values required to create a remote GitHub application aggregate.
    /// </summary>
    private readonly record struct RemoteAppInput(Guid UserId, string AppName, string GitUrl);
}