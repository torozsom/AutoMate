using Application.Data.Apps;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ApplicationServices.Data.Apps;

/// <summary>Returns detached owner-authorized metadata without loading credentials or environment values.</summary>
public sealed class DeploymentDetailsService(AutoMateDbContext db) : IDeploymentDetailsService
{
    /// <inheritdoc />
    public Task<Deployment?> GetAsync(Guid owner, Guid project, Guid deployment, CancellationToken token = default)
    {
        return db.Deployments.AsNoTracking().Include(d => d.CsProject).ThenInclude(p => p!.Application)
            .SingleOrDefaultAsync(d => d.Id == deployment && d.CsProject!.AppId == project &&
                                       d.CsProject.Application.UserId == owner, token);
    }
}