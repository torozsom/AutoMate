using Core.Entities;
using Core.Enums;
using Microsoft.EntityFrameworkCore;
using Services.Data;

namespace Services.Projects;

public class ProjectService(AutoMateDbContext context) : IProjectService
{

    /// <summary>
    /// Adds a local project for a user. Ensures that the same local path is not added multiple times for the same user.
    /// </summary>
    /// <param name="userId">The user ID of the project's owner.</param>
    /// <param name="projectName">The name of the project to be saved.</param>
    /// <param name="sourcePath">The source path of the project.</param>
    /// <returns></returns>
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

        await context.Projects.AddAsync(project);
        await context.SaveChangesAsync();
        return true;
    }

}