using Application.Orchestration;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.ApplicationServices.Orchestration;

/// <summary>Runs short SaaS launch tasks and renews their database leases across host instances.</summary>
public sealed class CloudDeploymentScheduler(
    IServiceScopeFactory scopeFactory,
    IOptions<CloudSaasOptions> options,
    ILogger<CloudDeploymentScheduler> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new List<Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            running.RemoveAll(task => task.IsCompleted);
            try
            {
                while (running.Count < options.Value.MaxActivePerWorker)
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var claim = await scope.ServiceProvider.GetRequiredService<CloudRunProcessor>()
                        .TryClaimAsync(stoppingToken);
                    if (claim is null) break;
                    running.Add(RunClaimedAsync(claim.Value.RunId, claim.Value.LeaseOwner, stoppingToken));
                }

                await Task.Delay(TimeSpan.FromSeconds(options.Value.SchedulerPollSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError("SaaS cloud admission failed; retrying after a bounded pause. Failure {FailureType}.",
                    ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        await Task.WhenAll(running);
    }

    /// <summary>Executes one claimed run in its own scope and maintains a separate lease heartbeat.</summary>
    private async Task RunClaimedAsync(Guid runId, Guid leaseOwner, CancellationToken stoppingToken)
    {
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = RenewLeaseAsync(runId, leaseOwner, runCancellation);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<CloudRunProcessor>()
                .ProcessAsync(runId, leaseOwner, runCancellation.Token);
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError("Cloud launch {RunId} stopped unexpectedly; its lease will recover. Failure {FailureType}.",
                runId, ex.GetType().Name);
        }
        finally
        {
            await runCancellation.CancelAsync();
            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "Lease renewal failed for cloud launch {RunId}; the run will recover. Failure {FailureType}.",
                    runId, ex.GetType().Name);
            }
        }
    }

    /// <summary>Keeps a slow Azure/GitHub preparation from being claimed by a second worker.</summary>
    private async Task RenewLeaseAsync(Guid runId, Guid leaseOwner, CancellationTokenSource runCancellation)
    {
        var cancellationToken = runCancellation.Token;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
                var updated = await db.CloudDeploymentRuns
                    .Where(item => item.Id == runId && item.LeaseOwner == leaseOwner &&
                                   item.Phase == CloudRunPhase.Preparing)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseUntil,
                        DateTimeOffset.UtcNow.AddMinutes(2)), cancellationToken);
                if (updated == 0)
                {
                    await runCancellation.CancelAsync();
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
        {
        }
        catch
        {
            await runCancellation.CancelAsync();
            throw;
        }
    }
}