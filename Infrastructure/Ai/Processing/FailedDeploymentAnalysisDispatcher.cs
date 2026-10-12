using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Ai;

/// <summary>
///     Recovers transactional failure wakeups without using UI notifications or diagnostic
///     payload storage.
/// </summary>
/// <param name="scopeFactory">Creates independent scopes for concurrent background work.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
public sealed class FailedDeploymentAnalysisDispatcher(
    IServiceScopeFactory scopeFactory,
    ILogger<FailedDeploymentAnalysisDispatcher> logger) : BackgroundService
{
    /// <summary>
    ///     Polls bounded batches; failures leave wakeups pending for the next fresh scope.
    /// </summary>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    /// <returns>A task representing the supervised service lifetime and cleanup.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
                var ids = await db.FailedDeploymentAnalysisEvents.AsNoTracking().Where(item => item.CompletedAt == null)
                    .OrderBy(item => item.CreatedAt).Select(item => item.DeploymentId).Take(100)
                    .ToListAsync(stoppingToken);
                foreach (var id in ids)
                {
                    await using var admissionScope = scopeFactory.CreateAsyncScope();
                    var services = admissionScope.ServiceProvider;
                    try
                    {
                        await DispatchAsync(services.GetRequiredService<AutoMateDbContext>(),
                            services.GetRequiredService<DeploymentAnalysisService>(),
                            services.GetRequiredService<TimeProvider>(),
                            id, stoppingToken);
                    }
                    catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning("Automatic analysis admission deferred: {FailureType}.",
                            exception.GetType().Name);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning("Automatic analysis admission deferred: {FailureType}.", exception.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    /// <summary>
    ///     Admission commits work and completion together; policy denials retire the wakeup without
    ///     later opt-in backfill.
    /// </summary>
    /// <param name="db">Application metadata context used for ownership, leases and persisted state.</param>
    /// <param name="service">Scoped owner-authorized analysis service used to admit or read work.</param>
    /// <param name="clock">Time source used for deterministic sampling and deadlines.</param>
    /// <param name="deploymentId">
    ///     The deployment identity; null is used only where legacy observations permit it.
    /// </param>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    internal static async Task DispatchAsync(AutoMateDbContext db, DeploymentAnalysisService service,
        TimeProvider clock,
        Guid deploymentId, CancellationToken token = default)
    {
        await service.RequestAutomaticAsync(deploymentId, token);
        // Successful admission already completes the marker atomically. A crash after a policy denial can safely retry.
        await db.FailedDeploymentAnalysisEvents
            .Where(item => item.DeploymentId == deploymentId && item.CompletedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.CompletedAt, clock.GetUtcNow()), token);
    }
}