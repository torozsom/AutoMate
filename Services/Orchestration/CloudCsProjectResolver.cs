using Core.DTO;
using Core.Entities;
using Microsoft.EntityFrameworkCore;
using Services.Data;

namespace Services.Orchestration;

/// <summary>
///     Resolves or creates the C# project record used by cloud deployment records.
/// </summary>
internal sealed class CloudCsProjectResolver(AutoMateDbContext dbContext)
{
    /// <summary>
    ///     Returns an explicitly selected C# project or creates a default web project for the remote application.
    /// </summary>
    public async Task<CsProject> GetOrCreateAsync(CloudDeploymentRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request.Config.CsProjectId != Guid.Empty)
            return await GetExistingCsProjectAsync(request.Config.CsProjectId, cancellationToken);

        var app = await dbContext.Applications
            .Include(a => a.CsProjects)
            .FirstOrDefaultAsync(a => a.Id == request.Config.ProjectId, cancellationToken);

        if (app == null)
            throw new InvalidOperationException($"Application with ID {request.Config.ProjectId} not found.");

        var csProject = app.CsProjects.FirstOrDefault(csp => csp.IsWebProject);
        if (csProject != null)
            return csProject;

        csProject = new CsProject
        {
            AppId = app.Id,
            Name = string.IsNullOrWhiteSpace(request.CsProjectName) ? app.Name : request.CsProjectName,
            Path = request.RepositoryRoot,
            IsWebProject = true
        };

        dbContext.CsProjects.Add(csProject);
        await dbContext.SaveChangesAsync(cancellationToken);
        return csProject;
    }

    /// <summary>
    ///     Loads an explicitly selected C# project or fails with the legacy error message.
    /// </summary>
    private async Task<CsProject> GetExistingCsProjectAsync(Guid csProjectId, CancellationToken cancellationToken)
    {
        var existingCsProject = await dbContext.CsProjects.FirstOrDefaultAsync(
            csp => csp.Id == csProjectId, cancellationToken);

        return existingCsProject ?? throw new InvalidOperationException(
            $"Project with ID {csProjectId} not found in the database.");
    }
}