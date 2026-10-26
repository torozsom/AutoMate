using Application.Data.Apps;
using Domain.Entities.Deployments;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ApplicationServices.Data.Apps;

/// <summary>
///     Returns detached owner-authorized metadata without loading credentials or environment
///     values.
/// </summary>
/// <param name="db">Application metadata context used for ownership, leases and persisted state.</param>
public sealed class DeploymentDetailsService(AutoMateDbContext db) : IDeploymentDetailsService
{
    /// <summary>
    ///     Reads a selected deployment and its project identity; never resolves the latest
    ///     deployment instead.
    /// </summary>
    /// <param name="owner">The internal owner account identity used for authorization.</param>
    /// <param name="project">The project identity used to scope the operation.</param>
    /// <param name="deployment">The exact deployment identity used to scope the operation.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning the exact owned historical deployment, or null when missing or
    ///     unauthorized.
    /// </returns>
    public Task<Deployment?> GetAsync(Guid owner, Guid project, Guid deployment, CancellationToken token = default)
    {
        return db.Deployments
            .AsNoTracking()
            .Include(d => d.CsProject)
            .ThenInclude(p => p!.Application)
            .SingleOrDefaultAsync(d =>
                d.Id == deployment
                && d.CsProject!.AppId == project
                && d.CsProject.Application.UserId == owner, token);
    }
}
