using Application.Abstractions.Diagnostics.History;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Diagnostics.Storage;

/// <summary>
///     Deletes expired redacted diagnostics in bounded batches.
/// </summary>
/// <param name="scopeFactory">Creates independent scopes for concurrent background work.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
public sealed class DeploymentDiagnosticRetentionService(
    IServiceScopeFactory scopeFactory,
    ILogger<DeploymentDiagnosticRetentionService> logger) : BackgroundService
{
    /// <summary>
    ///     Removes bounded batches of expired legacy diagnostic records.
    /// </summary>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    /// <returns>A task representing the supervised service lifetime and cleanup.</returns>
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

                    deleted = await scope.ServiceProvider
                        .GetRequiredService<IDeploymentDiagnosticStore>()
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
