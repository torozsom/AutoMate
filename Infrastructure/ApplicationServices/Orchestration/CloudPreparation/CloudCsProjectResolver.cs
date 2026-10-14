using Domain.DTO.Deployment;
using Domain.Entities.Projects;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ApplicationServices.Orchestration.CloudPreparation;

/// <summary>
///     Resolves or creates the C# project record used by cloud deployment records.
/// </summary>
/// <param name="dbContext">
///     Application metadata context used for ownership, leases and persisted state.
/// </param>
internal sealed class CloudCsProjectResolver(AutoMateDbContext dbContext)
{
    /// <summary>
    ///     Returns an explicitly selected C# project or creates a default web project for the
    ///     remote application.
    /// </summary>
    /// <param name="request">
    ///     Validated operation request containing the selected resource scope and metadata.
    /// </param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning the cs project projection derived from the verified inputs.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The requested project cannot be resolved within its verified owner/application scope.
    /// </exception>
    public async Task<CsProject> GetOrCreateAsync(CloudDeploymentRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request.Config.CsProjectId != Guid.Empty)
            return await GetExistingCsProjectAsync(request.Config.CsProjectId, request.Config.ProjectId,
                request.RequestingUserId, cancellationToken);

        var app = await dbContext.Applications
            .Include(a => a.CsProjects)
            .FirstOrDefaultAsync(a => a.Id == request.Config.ProjectId && a.UserId == request.RequestingUserId,
                cancellationToken);

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
    /// <param name="csProjectId">The exact C# project identity within the owned application.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="ownerId">The internal owner account identity used for authorization.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning the cs project projection derived from the verified inputs.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The exact selected C# project cannot be found for the owned application.
    /// </exception>
    private async Task<CsProject> GetExistingCsProjectAsync(Guid csProjectId, Guid projectId, Guid ownerId,
        CancellationToken cancellationToken)
    {
        var existingCsProject = await dbContext.CsProjects.FirstOrDefaultAsync(
            csp => csp.Id == csProjectId && csp.AppId == projectId && csp.Application.UserId == ownerId,
            cancellationToken);

        return existingCsProject ?? throw new InvalidOperationException(
            $"Project with ID {csProjectId} not found in the database.");
    }
}
