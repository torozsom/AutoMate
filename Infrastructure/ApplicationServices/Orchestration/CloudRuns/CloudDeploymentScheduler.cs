using Application.Orchestration.Cloud;
using Domain.Enums.Deployments;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.ApplicationServices.Orchestration.CloudRuns;

/// <summary>
///     Runs short SaaS launch tasks and renews their database leases across host instances.
/// </summary>
/// <param name="scopeFactory">Creates independent scopes for concurrent background work.</param>
/// <param name="options">Provides the configured limits and endpoint settings for this service.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
public sealed class CloudDeploymentScheduler(
    IServiceScopeFactory scopeFactory,
    IOptions<CloudSaasOptions> options,
    ILogger<CloudDeploymentScheduler> logger) : BackgroundService
{
    /// <summary>Samples deadlines and transitions at their original decision points.</summary>
    private readonly TimeProvider _clock = TimeProvider.System;

    /// <summary>
    ///     Uses an explicit execution clock while retaining the original constructor and system-clock defaults.
    /// </summary>
    /// <param name="scopeFactory">Creates independent scopes for concurrent background work.</param>
    /// <param name="options">Provides the configured limits and endpoint settings for this service.</param>
    /// <param name="logger">
    ///     Receives reviewed operational events without diagnostic payloads or credentials.
    /// </param>
    /// <param name="clock">The execution clock for the existing lease, retry and completion decisions.</param>
    public CloudDeploymentScheduler(IServiceScopeFactory scopeFactory,
        IOptions<CloudSaasOptions> options,
        ILogger<CloudDeploymentScheduler> logger,
        TimeProvider clock)
        : this(scopeFactory, options, logger)
    {
        _clock = clock;
    }

    /// <summary>
    ///     Claims durable cloud launches within shared admission and lease limits.
    /// </summary>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    /// <returns>A task representing the supervised service lifetime and cleanup.</returns>
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

    /// <summary>
    ///     Executes one claimed run in its own scope and maintains a separate lease heartbeat.
    /// </summary>
    /// <param name="runId">Verified durable cloud or provider workflow-run identity.</param>
    /// <param name="leaseOwner">Worker ownership identity for this operation.</param>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
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

    /// <summary>
    ///     Keeps a slow Azure/GitHub preparation from being claimed by a second worker.
    /// </summary>
    /// <param name="runId">Verified durable cloud or provider workflow-run identity.</param>
    /// <param name="leaseOwner">Worker ownership identity for this operation.</param>
    /// <param name="runCancellation">
    ///     Owns cancellation of the supervised work and must be disposed by its owner.
    /// </param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
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
                        _clock.GetUtcNow().AddMinutes(2)), cancellationToken);
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
