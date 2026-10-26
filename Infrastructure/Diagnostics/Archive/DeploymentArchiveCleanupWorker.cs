using Application.Abstractions.Diagnostics.History;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Diagnostics.Archive;

/// <summary>
///     Retries deletion of archived payloads after transactional metadata deletion.
/// </summary>
/// <param name="scopes">Creates independent scopes for concurrent background work.</param>
/// <param name="archive">Provides permanent owner-scoped redacted deployment history.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
public sealed class DeploymentArchiveCleanupWorker(
    IServiceScopeFactory scopes,
    IDeploymentArchive archive,
    ILogger<DeploymentArchiveCleanupWorker> logger) : BackgroundService
{
    /// <summary>
    ///     Processes durable archive-deletion outbox records after committed owner or project
    ///     deletion.
    /// </summary>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    /// <returns>A task representing the supervised service lifetime and cleanup.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await ProcessOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Archive cleanup unavailable: {FailureType}.", error.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    ///     Removes at most one bounded batch; failed payload deletion leaves its durable outbox
    ///     item for retry.
    /// </summary>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    public async Task ProcessOnceAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();

        var work = await db.DeploymentArchiveCleanups
            .OrderBy(x => x.CreatedAt)
            .Take(100)
            .ToArrayAsync(stoppingToken);

        foreach (var item in work)
        {
            if (await db.Applications.AnyAsync(x => x.Id == item.ProjectId, stoppingToken)) continue;
            try
            {
                await archive.DeleteProjectAsync(item.TenantId, item.ProjectId, stoppingToken);
                db.DeploymentArchiveCleanups.Remove(item);
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception error) when (error is not OperationCanceledException ||
                                          !stoppingToken.IsCancellationRequested)
            {
                db.Entry(item).State = EntityState.Unchanged;
                logger.LogWarning(error, "Archive cleanup unavailable: {FailureType}.", error.GetType().Name);
            }
        }
    }
}
