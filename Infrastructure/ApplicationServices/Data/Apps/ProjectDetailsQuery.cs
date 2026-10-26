using System.Data;
using Application.Data.Apps;
using Domain.Enums.Deployments;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ApplicationServices.Data.Apps;

/// <summary>
///     Separates current-state metadata, history aggregates and bounded history paging in owner-scoped SQL.
/// </summary>
/// <param name="db">The operation-owned metadata context.</param>
public sealed class ProjectDetailsQuery(AutoMateDbContext db) : IProjectDetailsQuery, IProjectActionQuery
{
    /// <summary>
    ///     Reads only action metadata, retaining timestamp ties for compatibility with existing latest selection.
    /// </summary>
    /// <param name="project">The application identity.</param>
    /// <param name="owner">The owner used by the entire metadata graph read.</param>
    /// <param name="token">Cancels the database operation.</param>
    /// <returns>The detached current graph, or null for an inaccessible application.</returns>
    public Task<Domain.Entities.Projects.Application?> GetAsync(Guid project, Guid owner,
        CancellationToken token = default)
    {
        return db.Applications
            .AsNoTracking()
            .Where(a => a.Id == project && a.UserId == owner)
            .Include(a => a.CsProjects)
            .ThenInclude(c => c.Configuration)
            .Include(a => a.CsProjects)
            .ThenInclude(c => c.Deployments
                .Where(d => !db.Deployments
                    .Any(newer => newer.CsProjectId == d.CsProjectId
                                  && newer.CreatedAt > d.CreatedAt)))
            .FirstOrDefaultAsync(token);
    }

    /// <summary>
    ///     Retains every deployment tied at the newest timestamp so existing in-memory latest selection stays compatible.
    /// </summary>
    /// <param name="project">The application identity.</param>
    /// <param name="owner">The owner used by metadata, totals and paging queries.</param>
    /// <param name="historyPage">The existing zero-based six-row page index.</param>
    /// <param name="token">Cancels the database operations.</param>
    /// <returns>The detached view, or null for an inaccessible application.</returns>
    public async Task<ProjectDetailsReadModel?> ReadAsync(Guid project, Guid owner, int historyPage,
        CancellationToken token = default)
    {
        // Keep the split reads coherent like the former single graph query; never replace a caller-owned transaction.
        await using var snapshot = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(db.Database.IsNpgsql()
                ? IsolationLevel.RepeatableRead
                : IsolationLevel.Serializable, token)
            : null;

        var app = await GetAsync(project, owner, token);
        if (app is null) return null;

        var history = db.Deployments
            .AsNoTracking()
            .Where(d => d.CsProject!.AppId == project && d.CsProject.Application.UserId == owner);

        var summary = await history
                          .GroupBy(_ => 1)
                          .Select(group => new ProjectDeploymentSummary(
                              group.Count(), group.Count(d => d.Outcome == DeploymentOutcome.Succeeded),
                              group.Count(d => d.Outcome == DeploymentOutcome.Failed),
                              group.Count(d => d.Outcome == DeploymentOutcome.Unknown),
                              group.Min(d => (DateTimeOffset?)d.CreatedAt)))
                          .SingleOrDefaultAsync(token)
                      ?? new ProjectDeploymentSummary(0, 0, 0, 0, null);

        var page = await history
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id)
            .Skip(historyPage * 6)
            .Take(6)
            .ToArrayAsync(token);

        // Restore the legacy navigation used by history labels without adding older rows to the current graph.
        var components = app.CsProjects.ToDictionary(component => component.Id);
        foreach (var deployment in page)
            deployment.CsProject = components.GetValueOrDefault(deployment.CsProjectId);

        return new ProjectDetailsReadModel(app, summary, page);
    }
}
