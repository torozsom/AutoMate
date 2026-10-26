using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.ApplicationServices.Orchestration.CloudRuns;

/// <summary>
///     Consumes webhook receipts and reconciles missing GitHub events across SaaS instances.
/// </summary>
/// <param name="scopeFactory">Creates independent scopes for concurrent background work.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
public sealed class CloudRunMonitorService(
    IServiceScopeFactory scopeFactory,
    ILogger<CloudRunMonitorService> logger) : BackgroundService
{
    /// <summary>
    ///     Reconciles verified remote workflows independently of the launch lease.
    /// </summary>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    /// <returns>A task representing the supervised service lifetime and cleanup.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
            try
            {
                var worked = false;
                for (var i = 0; i < 25; i++)
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    if (!await scope.ServiceProvider
                            .GetRequiredService<CloudRunMonitor>()
                            .ProcessNextDeliveryAsync(stoppingToken)) break;
                    worked = true;
                }

                await using (var scope = scopeFactory.CreateAsyncScope())
                {
                    worked |= await scope.ServiceProvider
                        .GetRequiredService<CloudRunMonitor>()
                        .ReconcileNextAsync(stoppingToken);
                }

                await Task.Delay(worked ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(15),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Cloud workflow monitor will retry after a bounded delay. Failure {FailureType}.",
                    ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
    }
}
