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
    /// <param name="projectName">The name of the project to be saved.</param>
    /// <param name="sourcePath">The source path of the project.</param>
    /// <returns>A task that returns true if the project was added successfully, or false if it already exists.</returns>
    public async Task<bool> AddLocalProjectAsync(Guid userId, string projectName, string sourcePath)
    {
        var alreadyExists = await context.Projects
            .AnyAsync(p => p.UserId == userId && p.SourceType == SourceType.Local && p.SourcePathOrUrl == sourcePath);

        if (alreadyExists)
            return false;

        var project = new Project
        {
            UserId = userId,
            Name = projectName,
            SourceType = SourceType.Local,
            SourcePathOrUrl = sourcePath,
        };

        context.Projects.Add(project);
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
        var alreadyExists = await context.Projects
            .AnyAsync(p => p.UserId == userId && p.SourceType == SourceType.Remote && p.SourcePathOrUrl == gitUrl);

        if (alreadyExists)
            return false;

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

}