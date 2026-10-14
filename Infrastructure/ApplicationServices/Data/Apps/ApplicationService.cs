using Domain.DTO;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Data.Apps;

/// <summary>
///     Service class responsible for managing projects within the application. It handles
///     operations such as adding local and GitHub projects, and interacting with the database
///     to persist project information.
/// </summary>
/// <param name="context">The database context.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
public sealed class ApplicationService(AutoMateDbContext context, ILogger<ApplicationService> logger)
    : IApplicationService
{
    /// <summary>Default .NET version assigned to newly discovered C# projects.</summary>
    private const string DefaultDotNetVersion = "10.0";

    /// <summary>Default local port assigned to newly discovered C# web projects.</summary>
    private const int DefaultExposedPort = 8080;


    /// <summary>
    ///     Adds a local project to the database for a specific user. Checks if a project with the
    ///     same source path already exists for the user before adding.
    /// </summary>
    /// <param name="userId">The user ID of the project's owner.</param>
    /// <param name="project">The project information to be saved.</param>
    /// <param name="csproject">The C# project file information.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>
    ///     A task that returns true if the project was added successfully, or false if it already
    ///     exists.
    /// </returns>
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
                "Local project configuration saved for project {ProjectId}, user {UserId}.",
                app.Id, userId);
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


    /// <summary>
    ///     Adds a GitHub project to the database for a specific user. Checks if a project with the
    ///     same source URL already exists for the user before adding.
    /// </summary>
    /// <param name="userId">The user ID of the project's owner.</param>
    /// <param name="appName">The name of the project to be saved.</param>
    /// <param name="gitUrl">The git URL of the remote repository.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>
    ///     A task that returns true if the project was added successfully, or false if it already
    ///     exists.
    /// </returns>
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

            var app = CreateRemoteApplication(input);
            context.Applications.Add(app);
            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "GitHub project saved for project {ProjectId}, user {UserId}.",
                app.Id,
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


    /// <summary>
    ///     Retrieves a list of projects associated with a specific user. The method queries the
    ///     database for projects that match the provided user ID and returns them as a list.
    /// </summary>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A task returning a list of projects belonging to the user.</returns>
    public async Task<List<Domain.Entities.Application>> GetUserAppsAsync(Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await QueryApplicationsWithProjectDeployments()
            .Where(a => a.UserId == userId)
            .ToListAsync(cancellationToken);
    }


    /// <summary>
    ///     Deletes a project associated with a specific user from the database. Checks if the
    ///     project exists before attempting to delete it.
    /// </summary>
    /// <param name="appId">The unique identifier of the project to be deleted.</param>
    /// <param name="userId">The unique identifier of the user who owns the project.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>
    ///     A task that returns true if the project was deleted successfully, or false if the
    ///     project does not exist.
    /// </returns>
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

            // PostgreSQL also handles account cascades through the transactional deletion trigger.
            if (!context.Database.IsNpgsql())
                context.DeploymentArchiveCleanups.Add(new DeploymentArchiveCleanup
                {
                    TenantId = userId,
                    ProjectId = appId
                });
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


    /// <summary>
    ///     Retrieves a specific project by its ID for a given user. If the project is found, it
    ///     includes the associated C# projects in the result.
    /// </summary>
    /// <param name="appId">The unique identifier of the project to retrieve.</param>
    /// <param name="userId">The unique identifier of the user who owns the project.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A task that returns the project if found, or null if not found.</returns>
    public async Task<Domain.Entities.Application?> GetAppByIdAsync(Guid appId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await QueryApplicationsWithProjectDeployments()
            .FirstOrDefaultAsync(a => a.Id == appId && a.UserId == userId, cancellationToken);
    }

    /// <summary>
    ///     Updates the first configured project for legacy application-level callers.
    /// </summary>
    /// <param name="appId">The project identity used for authorization or correlation.</param>
    /// <param name="userId">The internal AutoMate user identity.</param>
    /// <param name="consented">
    ///     Whether the owner permits bounded diagnostic data egress to the configured AI provider.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning whether consent was updated for the specified owned project.</returns>
    public async Task<bool> SetAiDiagnosticEgressConsentAsync(Guid appId, Guid userId, bool consented,
        CancellationToken cancellationToken = default)
    {
        var configuration = await context.Applications.Where(item => item.Id == appId && item.UserId == userId)
            .SelectMany(item => item.CsProjects).Select(item => item.Configuration)
            .FirstOrDefaultAsync(item => item != null, cancellationToken);
        if (configuration is null)
        {
            logger.LogWarning("Diagnostic egress consent change denied for project {ProjectId}, user {UserId}.", appId,
                userId);
            return false;
        }

        configuration.AiDiagnosticEgressConsented = consented;
        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Diagnostic egress consent changed for project {ProjectId}, user {UserId}: {ConsentState}.",
            appId, userId, consented ? "enabled" : "disabled");
        return true;
    }

    /// <summary>
    ///     Updates exact owned project consent, creating missing remote configuration with default
    ///     settings.
    /// </summary>
    /// <param name="appId">The owned application identity.</param>
    /// <param name="userId">The internal AutoMate user identity.</param>
    /// <param name="csProjectId">The exact C# project identity within the owned application.</param>
    /// <param name="consented">
    ///     Whether the owner permits bounded diagnostic data egress to the configured AI provider.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning whether consent was updated for the specified owned project.</returns>
    public async Task<bool> SetAiDiagnosticEgressConsentAsync(Guid appId, Guid userId, Guid csProjectId, bool consented,
        CancellationToken cancellationToken = default)
    {
        var project = await context.CsProjects
            .Include(item => item.Configuration).Include(item => item.Application)
            .FirstOrDefaultAsync(item => item.Id == csProjectId && item.AppId == appId &&
                                         item.Application.UserId == userId, cancellationToken);
        if (project is null || (project.Configuration is null && project.Application.SourceType != SourceType.Remote))
        {
            logger.LogWarning("Diagnostic egress consent change denied for project {ProjectId}, user {UserId}.", appId,
                userId);
            return false;
        }

        // Remote projects are created without deployment configuration. Consent must still be owner-editable.
        if (project.Configuration is null)
        {
            project.Configuration = new Configuration
            { CsProjectId = project.Id, DotNetVersion = DefaultDotNetVersion };
            context.Set<Configuration>().Add(project.Configuration);
        }

        project.Configuration.AiDiagnosticEgressConsented = consented;
        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Diagnostic egress consent changed for project {ProjectId}, user {UserId}: {ConsentState}.",
            appId, userId, consented ? "enabled" : "disabled");
        return true;
    }


    /// <summary>
    ///     Builds the reusable read query for applications with projects and deployment history.
    /// </summary>
    /// <returns>
    ///     The query applications with project deployments selection with its documented bounds and
    ///     ordering.
    /// </returns>
    private IQueryable<Domain.Entities.Application> QueryApplicationsWithProjectDeployments()
    {
        return context.Applications
            .AsNoTracking()
            .Include(a => a.CsProjects)
            .ThenInclude(csp => csp.Configuration)
            .Include(a => a.CsProjects)
            .ThenInclude(csp => csp.Deployments)
            .AsSingleQuery();
    }

    /// <summary>
    ///     Finds an existing local application and eagerly loads its C# projects for duplicate
    ///     checks.
    /// </summary>
    /// <param name="input">Normalized registration or project-import input.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning the domain.entities.application projection derived from the verified
    ///     inputs, or null when no value is available.
    /// </returns>
    private async Task<Domain.Entities.Application?> GetLocalAppWithProjectsAsync(LocalAppInput input,
        CancellationToken cancellationToken)
    {
        return await context.Applications
            .Include(a => a.CsProjects)
            .FirstOrDefaultAsync(a => a.UserId == input.UserId
                                      && a.SourceType == SourceType.Local
                                      && a.SourcePathOrUrl == input.SourcePath, cancellationToken);
    }

    /// <summary>
    ///     Checks whether a remote GitHub application already exists for the user and repository
    ///     URL.
    /// </summary>
    /// <param name="input">Normalized registration or project-import input.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning whether the documented remote app exists async condition holds.</returns>
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
    /// <param name="input">Normalized registration or project-import input.</param>
    /// <returns>The domain.entities.application projection derived from the verified inputs.</returns>
    private static Domain.Entities.Application CreateLocalApplication(LocalAppInput input)
    {
        return new Domain.Entities.Application
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
    /// <param name="input">Normalized registration or project-import input.</param>
    /// <returns>The domain.entities.application projection derived from the verified inputs.</returns>
    private static Domain.Entities.Application CreateRemoteApplication(RemoteAppInput input)
    {
        return new Domain.Entities.Application
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
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="name">The recorded display name or fixed metric identity.</param>
    /// <param name="path">Filesystem or provider entry path used by this operation.</param>
    /// <param name="isWebProject">Whether the scanned C# project is a web application.</param>
    /// <returns>The cs project projection derived from the verified inputs.</returns>
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
    /// <param name="app">The application whose project dependencies are being analyzed.</param>
    /// <param name="csProjectPath">The local C# project path used to locate Compose resources.</param>
    /// <returns>Whether the documented cs project condition holds.</returns>
    private static bool ContainsCsProject(Domain.Entities.Application app, string csProjectPath)
    {
        return app.CsProjects.Any(csp => csp.Path == csProjectPath);
    }

    /// <summary>
    ///     Validates and normalizes local application input from discovery DTOs.
    /// </summary>
    /// <param name="userId">The internal AutoMate user identity.</param>
    /// <param name="project">The owned project identity or normalized local import metadata.</param>
    /// <param name="csProject">The selected C# project and its recorded metadata.</param>
    /// <param name="input">Normalized registration or project-import input.</param>
    /// <returns>True when validation or acquisition succeeds; otherwise false.</returns>
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
    /// <param name="userId">The internal AutoMate user identity.</param>
    /// <param name="appName">Application name used for deployment artifacts.</param>
    /// <param name="gitUrl">The remote Git repository URL.</param>
    /// <param name="input">Normalized registration or project-import input.</param>
    /// <returns>True when validation or acquisition succeeds; otherwise false.</returns>
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
    /// <param name="UserId">The internal AutoMate user identity.</param>
    /// <param name="ProjectName">The project name used for deployment artifacts or Compose lookup.</param>
    /// <param name="SourcePath">The Docker build-context directory.</param>
    /// <param name="CsProjectName">The selected C# project name used for generated artifacts.</param>
    /// <param name="CsProjectPath">The local C# project path used to locate Compose resources.</param>
    /// <param name="IsWebProject">Whether the scanned C# project is a web application.</param>
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
    /// <param name="UserId">The internal AutoMate user identity.</param>
    /// <param name="AppName">Application name used for deployment artifacts.</param>
    /// <param name="GitUrl">The remote Git repository URL.</param>
    private readonly record struct RemoteAppInput(Guid UserId, string AppName, string GitUrl);
}
