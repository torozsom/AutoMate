using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.ApplicationServices.Orchestration;

/// <summary>Consumes webhook receipts and reconciles missing GitHub events across SaaS instances.</summary>
public sealed class CloudRunMonitorService(IServiceScopeFactory scopeFactory,
    ILogger<CloudRunMonitorService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var worked = false;
                for (var i = 0; i < 25; i++)
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    if (!await scope.ServiceProvider.GetRequiredService<CloudRunMonitor>()
                            .ProcessNextDeliveryAsync(stoppingToken)) break;
                    worked = true;
                }
                await using (var scope = scopeFactory.CreateAsyncScope())
                    worked |= await scope.ServiceProvider.GetRequiredService<CloudRunMonitor>()
                        .ReconcileNextAsync(stoppingToken);
                await Task.Delay(worked ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(15),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cloud workflow monitor will retry after a bounded delay.");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }
}
