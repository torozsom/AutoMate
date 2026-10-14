using System.Diagnostics;
using Application.Abstractions.Ai.Context;
using Application.Abstractions.Ai.Execution;
using Application.Abstractions.Ai.Policy;
using Application.Abstractions.Ai.Results;
using Application.Ai.Policy;
using Application.Diagnostics;
using Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Ai.Processing;

/// <summary>
///     Processes queued diagnoses in bounded independent scopes; every slot retains lease
///     renewal and result fencing.
/// </summary>
/// <param name="scopeFactory">Creates independent scopes for concurrent background work.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
/// <param name="clock">Time source used for deterministic sampling and deadlines.</param>
/// <param name="options">Provides the configured limits and endpoint settings for this service.</param>
public sealed class DeploymentAnalysisWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<DeploymentAnalysisWorker> logger,
    TimeProvider clock,
    IOptions<AiAnalysisOptions> options) : BackgroundService
{
    /// <summary>
    ///     Runs bounded analysis slots with fenced ownership, current egress approval and safe
    ///     result persistence.
    /// </summary>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    /// <returns>A task representing the supervised service lifetime and cleanup.</returns>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var concurrency = options.Value.MaximumConcurrency;
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(concurrency, AiAnalysisOptions.MaximumSupportedConcurrency);
        return Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => RunConsumerAsync(stoppingToken)));
    }

    /// <summary>
    ///     A slot claims only when free, awaits all lease cleanup before reuse and never shares a
    ///     processing scope.
    /// </summary>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private async Task RunConsumerAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var queue = scope.ServiceProvider.GetRequiredService<IDeploymentAnalysisQueue>();
                var work = await AnalysisTelemetry.RunAsync(AnalysisOperation.Claim, null, null, stoppingToken,
                    () => queue.ClaimNextAsync(stoppingToken),
                    result =>
                    {
                        if (result is null) return AnalysisOutcome.Empty;
                        Activity.Current?.SetTag("deployment.id", result.DeploymentId);
                        Activity.Current?.SetTag("deployment.analysis.id", result.AnalysisId);
                        return AnalysisOutcome.Completed;
                    });
                if (work is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                await RunLeasedAsync(scope.ServiceProvider, work, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug("Deployment analysis worker is stopping.");
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Deployment analysis worker recovered: {FailureType}.",
                    exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
    }

    /// <summary>
    ///     Renews through independent scopes and gives interrupted work a bounded best-effort
    ///     release on shutdown.
    /// </summary>
    /// <param name="services">Resolves the current scoped operation dependencies.</param>
    /// <param name="work">The detached analysis claim and its lease ownership token.</param>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    internal async Task RunLeasedAsync(IServiceProvider services, DeploymentAnalysisWorkItem work,
        CancellationToken stoppingToken)
    {
        using var owned = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = RenewLeaseAsync(work, owned, heartbeatStop.Token);
        var finished = false;
        try
        {
            await ProcessAsync(services, work, owned.Token);
            finished = true;
        }
        catch (OperationCanceledException) when
            (owned.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            OperationalLog.Record(logger, AuditOperation.Analysis, AuditOutcome.Discarded);
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested)
            {
            }

            if (!finished)
            {
                // Cleanup is a separate bounded operation because the caller's processing token may be canceled.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    await AnalysisTelemetry.RunAsync(AnalysisOperation.Release, work.DeploymentId, work.AnalysisId,
                        cleanup.Token, () => scope.ServiceProvider.GetRequiredService<IDeploymentAnalysisQueue>()
                            .ReleaseAsync(work, cleanup.Token),
                        released => released ? AnalysisOutcome.Completed : AnalysisOutcome.Discarded);
                }
                catch (Exception)
                {
                    /* Expiry remains the durable recovery path when shutdown cleanup fails. */
                }
            }
        }
    }

    /// <summary>
    ///     A failed/expired renewal cancels local processing; final database fencing rejects late
    ///     uncooperative results.
    /// </summary>
    /// <param name="work">The detached analysis claim and its lease ownership token.</param>
    /// <param name="owned">Owns cancellation of the supervised work and must be disposed by its owner.</param>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private async Task RenewLeaseAsync(DeploymentAnalysisWorkItem work, CancellationTokenSource owned,
        CancellationToken token)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp((work.LeaseUntil - clock.GetUtcNow()).TotalSeconds / 3, 1, 10));
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(interval, clock, token);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                await using var scope = scopeFactory.CreateAsyncScope();
                if (await AnalysisTelemetry.RunAsync(AnalysisOperation.Renew, work.DeploymentId, work.AnalysisId,
                        deadline.Token, () => scope.ServiceProvider.GetRequiredService<IDeploymentAnalysisQueue>()
                            .RenewAsync(work, deadline.Token),
                        renewed => renewed ? AnalysisOutcome.Completed : AnalysisOutcome.Discarded)) continue;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                /* Fail closed on uncertain ownership; another worker can recover after expiry. */
            }

            await owned.CancelAsync();
            return;
        }
    }

    /// <summary>
    ///     Stores only a complete validated response under its current lease; failures never copy
    ///     exception messages.
    /// </summary>
    /// <param name="services">Resolves the current scoped operation dependencies.</param>
    /// <param name="work">The detached analysis claim and its lease ownership token.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    internal static Task ProcessAsync(IServiceProvider services, DeploymentAnalysisWorkItem work,
        CancellationToken cancellationToken)
    {
        return AnalysisTelemetry.RunAsync(AnalysisOperation.Process, work.DeploymentId, work.AnalysisId,
            cancellationToken, () =>
            {
                if (work.LeaseId == Guid.Empty) return Task.FromResult(AnalysisOutcome.Discarded);
                var operationLogger = services.GetRequiredService<ILogger<DeploymentAnalysisWorker>>();
                var db = services.GetRequiredService<AutoMateDbContext>();
                var operationClock = services.GetRequiredService<TimeProvider>();
                var processor = new DeploymentAnalysisProcessor(db, operationClock, operationLogger,
                    new Lazy<IOptionsMonitor<AiAnalysisOptions>>(() =>
                        services.GetRequiredService<IOptionsMonitor<AiAnalysisOptions>>()),
                    new Lazy<IAnalysisEgressAuthorizer>(() => services.GetRequiredService<IAnalysisEgressAuthorizer>()),
                    new Lazy<IDeploymentAnalysisContextBuilder>(() =>
                        services.GetRequiredService<IDeploymentAnalysisContextBuilder>()),
                    new Lazy<IAnalysisBudgetGuard>(() => services.GetRequiredService<IAnalysisBudgetGuard>()),
                    new Lazy<ILlmAnalysisProvider>(() => services.GetRequiredService<ILlmAnalysisProvider>()),
                    new Lazy<IAnalysisResultValidator>(() => services.GetRequiredService<IAnalysisResultValidator>()));
                return processor.ProcessAsync(work, cancellationToken);
            }, outcome => outcome);
    }
}

/// <summary>
///     Fixed safe terminal outcome after the configured number of interrupted acquisitions.
/// </summary>
internal sealed class AnalysisRecoveryExhaustedException() : Exception("Analysis recovery attempts are exhausted.");

/// <summary>
///     Safe terminal outcome when reloaded configuration no longer permits a queued retry.
/// </summary>
internal sealed class AnalysisRetryExhaustedException() : Exception("AI provider retry limit reached.");
