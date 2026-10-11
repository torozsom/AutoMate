using Application.Abstractions.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Diagnostics;

/// <summary>Deletes expired redacted diagnostics in bounded batches.</summary>
public sealed class DeploymentDiagnosticRetentionService(
    IServiceScopeFactory scopeFactory,
    ILogger<DeploymentDiagnosticRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                int deleted;
                var batches = 0;
                do
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    deleted = await scope.ServiceProvider.GetRequiredService<IDeploymentDiagnosticStore>()
                        .DeleteExpiredAsync(1_000, stoppingToken);
                    batches++;
                } while (deleted == 1_000 && batches < 10 && !stoppingToken.IsCancellationRequested);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("Expired deployment diagnostic cleanup failed. Failure {FailureType}.",
                    exception.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}