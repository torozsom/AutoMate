using System.Diagnostics;
using System.Text.Json;
using Application.Abstractions.Ai;
using Application.Ai;
using Application.Diagnostics;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Ai;

/// <summary>Processes queued diagnoses in bounded independent scopes; every slot retains lease renewal and result fencing.</summary>
public sealed class DeploymentAnalysisWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<DeploymentAnalysisWorker> logger,
    TimeProvider clock,
    IOptions<AiAnalysisOptions> options) : BackgroundService
{
    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var concurrency = options.Value.MaximumConcurrency;
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(concurrency, AiAnalysisOptions.MaximumSupportedConcurrency);
        return Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => RunConsumerAsync(stoppingToken)));
    }

    /// <summary>A slot claims only when free, awaits all lease cleanup before reuse and never shares a processing scope.</summary>
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

    /// <summary>Renews through independent scopes and gives interrupted work a bounded best-effort release on shutdown.</summary>
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

    /// <summary>A failed/expired renewal cancels local processing; final database fencing rejects late uncooperative results.</summary>
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

    /// <summary>Stores only a complete validated response under its current lease; failures never copy exception messages.</summary>
    internal static Task ProcessAsync(IServiceProvider services, DeploymentAnalysisWorkItem work,
        CancellationToken cancellationToken)
    {
        return AnalysisTelemetry.RunAsync(AnalysisOperation.Process, work.DeploymentId, work.AnalysisId,
            cancellationToken,
            () => ProcessCoreAsync(services, work, cancellationToken), outcome => outcome);
    }

    /// <summary>Returns only the committed outcome; stale writes and retries cannot report a completed analysis.</summary>
    private static async Task<AnalysisOutcome> ProcessCoreAsync(IServiceProvider services,
        DeploymentAnalysisWorkItem work,
        CancellationToken cancellationToken)
    {
        if (work.LeaseId == Guid.Empty) return AnalysisOutcome.Discarded;
        var analysisId = work.AnalysisId;
        var logger = services.GetRequiredService<ILogger<DeploymentAnalysisWorker>>();
        var db = services.GetRequiredService<AutoMateDbContext>();
        var clock = services.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();
        var analysis = await db.AiDeploymentAnalyses.AsNoTracking().FirstOrDefaultAsync(
            item => item.Id == analysisId && item.ExpiresAt > now, cancellationToken);
        if (analysis is null) return AnalysisOutcome.Discarded;
        using var correlation = OperationalLog.BeginCorrelation(logger, analysis.DeploymentId, analysisId);
        OperationalLog.Record(logger, AuditOperation.Analysis, AuditOutcome.Started);
        analysis.Status = AiAnalysisStatus.Running;
        analysis.StartedAt = clock.GetUtcNow();
        if (await db.AiDeploymentAnalyses.Where(item => item.Id == analysisId && item.ExpiresAt > now &&
                                                        (item.Status == AiAnalysisStatus.Queued ||
                                                         item.Status == AiAnalysisStatus.Running) &&
                                                        db.DeploymentAnalysisWorkItems.Any(row =>
                                                            row.AnalysisId == analysisId &&
                                                            row.LeaseId == work.LeaseId &&
                                                            row.CompletedAt == null && row.LeaseUntil > now))
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, AiAnalysisStatus.Running)
                    .SetProperty(item => item.StartedAt, now)
                    .SetProperty(item => item.RetryCount, work.ProviderRetryCount), cancellationToken) ==
            0) return AnalysisOutcome.Discarded;
        var providerStarted = false;
        try
        {
            if (work.Attempt >
                Math.Clamp(
                    services.GetRequiredService<IOptionsMonitor<AiAnalysisOptions>>().CurrentValue
                        .MaximumRecoveryAttempts, 1, 10))
                throw new AnalysisRecoveryExhaustedException();
            if (work.ProviderRetryCount >
                Math.Clamp(
                    services.GetRequiredService<IOptionsMonitor<AiAnalysisOptions>>().CurrentValue
                        .MaximumProviderRetries, 0, 5))
                throw new AnalysisRetryExhaustedException();
            var egress = services.GetRequiredService<IAnalysisEgressAuthorizer>();
            if (!await egress.AuthorizeAsync(analysis.DeploymentId, analysis.Trigger, cancellationToken))
                throw new AnalysisProviderUnavailableException();
            var context = await AnalysisTelemetry.RunAsync(AnalysisOperation.Context, analysis.DeploymentId, analysisId,
                cancellationToken, () => services.GetRequiredService<IDeploymentAnalysisContextBuilder>()
                    .BuildAsync(analysis.DeploymentId, AssessmentSelection.Read(analysis.RequestedSelectionJson),
                        cancellationToken),
                result => string.IsNullOrWhiteSpace(result.Text) ? AnalysisOutcome.Empty : AnalysisOutcome.Completed);
            analysis.AssessmentProvenanceJson =
                context.Provenance is null ? null : JsonSerializer.Serialize(context.Provenance);
            if (string.IsNullOrWhiteSpace(context.Text))
                throw new AnalysisProviderUnavailableException(AnalysisSkipReason.UnsupportedData);
            var evidence = Array.AsReadOnly(context.EvidenceReferences.ToArray());
            now = clock.GetUtcNow();
            if (!await db.AiDeploymentAnalyses.AsNoTracking().AnyAsync(
                    item => item.Id == analysisId && item.ExpiresAt > now &&
                            db.DeploymentAnalysisWorkItems.Any(row =>
                                row.AnalysisId == analysisId && row.LeaseId == work.LeaseId &&
                                row.CompletedAt == null && row.LeaseUntil > now), cancellationToken))
                return AnalysisOutcome.Discarded;
            if (!await egress.AuthorizeAsync(analysis.DeploymentId, analysis.Trigger, cancellationToken))
                throw new AnalysisProviderUnavailableException();
            var spendSettings = services.GetRequiredService<IOptionsMonitor<AiAnalysisOptions>>().CurrentValue;
            var denied = await services.GetRequiredService<IAnalysisBudgetGuard>()
                .ReserveAttemptAsync(work, cancellationToken);
            if (!ReferenceEquals(spendSettings,
                    services.GetRequiredService<IOptionsMonitor<AiAnalysisOptions>>().CurrentValue))
                denied = AnalysisSkipReason.Unavailable;
            if (denied is { } reason)
            {
                OperationalLog.Record(logger, AuditOperation.Analysis, AuditOutcome.Denied);
                throw new AnalysisProviderUnavailableException(reason);
            }

            now = clock.GetUtcNow();
            if (!await db.DeploymentAnalysisWorkItems.AsNoTracking().AnyAsync(item =>
                        item.AnalysisId == analysisId && item.LeaseId == work.LeaseId && item.CompletedAt == null &&
                        item.LeaseUntil > now && item.Analysis.ExpiresAt > now &&
                        item.Analysis.Status == AiAnalysisStatus.Running,
                    cancellationToken)) return AnalysisOutcome.Discarded;
            providerStarted = true;
            OperationalLog.Record(logger, AuditOperation.AnalysisProvider, AuditOutcome.Started);
            var response = await AnalysisTelemetry.RunAsync(AnalysisOperation.Provider, analysis.DeploymentId,
                analysisId,
                cancellationToken, async () =>
                {
                    var result = await services.GetRequiredService<ILlmAnalysisProvider>()
                        .AnalyzeAsync(
                            new LlmAnalysisRequest(context.Text, evidence, analysis.DeploymentId, analysis.Trigger,
                                context.Provenance?.EffectiveKind ?? AssessmentKind.Automatic),
                            cancellationToken);
                    result = services.GetRequiredService<IAnalysisResultValidator>().Validate(result);
                    result = AnalysisEvidence.Validate(result, evidence);
                    AnalysisTelemetry.Usage(result);
                    return result;
                });
            OperationalLog.Record(logger, AuditOperation.AnalysisProvider, AuditOutcome.Completed);
            analysis.Provider = response.Provider;
            analysis.Model = response.Model;
            analysis.Summary = response.Summary;
            analysis.AssessmentSectionsJson =
                response.Sections is null ? null : JsonSerializer.Serialize(response.Sections);
            analysis.RecommendedStepsJson = JsonSerializer.Serialize(response.RecommendedSteps);
            analysis.EvidenceReferencesJson = JsonSerializer.Serialize(response.EvidenceReferences);
            analysis.RequestedModel = response.RequestedModel;
            analysis.ModelVersion = response.ModelVersion;
            analysis.PromptVersion = response.PromptVersion;
            analysis.ResultSchemaVersion = response.ResultSchemaVersion;
            analysis.InputTokens = response.InputTokens;
            analysis.OutputTokens = response.OutputTokens;
            analysis.EstimatedCost = response.EstimatedCost;
            analysis.CostCurrency = response.CostCurrency;
            analysis.FailureCode = null;
            analysis.Status = AiAnalysisStatus.Completed;
            analysis.CompletedAt = clock.GetUtcNow();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            OperationalLog.Record(logger, AuditOperation.Analysis, AuditOutcome.Canceled);
            throw;
        }
        catch (TransientAnalysisProviderException error) when (providerStarted)
        {
            logger.LogWarning(error, "AI analysis execution failed: {FailureType}.", error.GetType().Name);
            OperationalLog.Record(logger, AuditOperation.AnalysisProvider, AuditOutcome.Failed);
            var delay = AnalysisRetryPolicy.Delay(
                services.GetRequiredService<IOptionsMonitor<AiAnalysisOptions>>().CurrentValue,
                work.ProviderRetryCount, error.RetryAfterSeconds);
            if (delay is { } wait && clock.GetUtcNow().Add(wait) < analysis.ExpiresAt)
            {
                var scheduled = await AnalysisTelemetry.RunAsync(AnalysisOperation.Retry, analysis.DeploymentId,
                    analysisId,
                    cancellationToken, () => ScheduleRetryAsync(db, clock, work, wait, cancellationToken),
                    result => result ? AnalysisOutcome.RetryScheduled : AnalysisOutcome.Discarded);
                OperationalLog.Record(logger, AuditOperation.Analysis,
                    scheduled ? AuditOutcome.RetryScheduled : AuditOutcome.Discarded);
                return scheduled ? AnalysisOutcome.RetryScheduled : AnalysisOutcome.Discarded;
            }

            analysis.Status = AiAnalysisStatus.Failed;
            analysis.FailureCode = "retry_exhausted";
            analysis.CompletedAt = clock.GetUtcNow();
        }
        catch (AnalysisRetryExhaustedException)
        {
            analysis.Status = AiAnalysisStatus.Failed;
            analysis.FailureCode = "retry_exhausted";
            analysis.CompletedAt = clock.GetUtcNow();
        }
        catch (AnalysisRecoveryExhaustedException)
        {
            analysis.Status = AiAnalysisStatus.Failed;
            analysis.FailureCode = "recovery_exhausted";
            analysis.Summary = "AI analysis stopped after repeated worker interruptions.";
            analysis.CompletedAt = clock.GetUtcNow();
        }
        catch (AnalysisProviderUnavailableException unavailable)
        {
            if (providerStarted)
                OperationalLog.Record(logger, AuditOperation.AnalysisProvider, AuditOutcome.Unavailable);
            analysis.Status = AiAnalysisStatus.Skipped;
            analysis.FailureCode = AnalysisSkipPolicy.Code(unavailable.Reason);
            analysis.Summary = AnalysisSkipPolicy.Message(analysis.FailureCode);
            analysis.CompletedAt = clock.GetUtcNow();
        }
        catch (InvalidAnalysisResultException)
        {
            if (providerStarted)
                OperationalLog.Record(logger, AuditOperation.AnalysisProvider, AuditOutcome.InvalidResult);
            analysis.Status = AiAnalysisStatus.Failed;
            analysis.FailureCode = "invalid_response";
            analysis.CompletedAt = clock.GetUtcNow();
        }
        catch (Exception error)
        {
            logger.LogError(error, "AI analysis execution failed: {FailureType}.", error.GetType().Name);
            if (providerStarted) OperationalLog.Record(logger, AuditOperation.AnalysisProvider, AuditOutcome.Failed);
            analysis.Status = AiAnalysisStatus.Failed;
            analysis.FailureCode = "provider_failure";
            analysis.CompletedAt = clock.GetUtcNow();
        }

        // Conditional writes prevent expired/deleted work from publishing a late result.
        using var publication = AnalysisTelemetry.Start(AnalysisOperation.Publish, analysis.DeploymentId, analysisId,
            cancellationToken);
        now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var stored = await db.AiDeploymentAnalyses.Where(item => item.Id == analysisId && item.ExpiresAt > now &&
                                                                 item.Status == AiAnalysisStatus.Running &&
                                                                 db.DeploymentAnalysisWorkItems.Any(row =>
                                                                     row.AnalysisId == analysisId &&
                                                                     row.LeaseId == work.LeaseId &&
                                                                     row.CompletedAt == null && row.LeaseUntil > now))
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.Provider, analysis.Provider)
                .SetProperty(item => item.Model, analysis.Model)
                .SetProperty(item => item.Summary, analysis.Summary)
                .SetProperty(item => item.AssessmentProvenanceJson, analysis.AssessmentProvenanceJson)
                .SetProperty(item => item.AssessmentSectionsJson, analysis.AssessmentSectionsJson)
                .SetProperty(item => item.RecommendedStepsJson, analysis.RecommendedStepsJson)
                .SetProperty(item => item.EvidenceReferencesJson, analysis.EvidenceReferencesJson)
                .SetProperty(item => item.RequestedModel, analysis.RequestedModel)
                .SetProperty(item => item.ModelVersion, analysis.ModelVersion)
                .SetProperty(item => item.PromptVersion, analysis.PromptVersion)
                .SetProperty(item => item.ResultSchemaVersion, analysis.ResultSchemaVersion)
                .SetProperty(item => item.InputTokens, analysis.InputTokens)
                .SetProperty(item => item.OutputTokens, analysis.OutputTokens)
                .SetProperty(item => item.EstimatedCost, analysis.EstimatedCost)
                .SetProperty(item => item.CostCurrency, analysis.CostCurrency)
                .SetProperty(item => item.FailureCode, analysis.FailureCode)
                .SetProperty(item => item.Status, analysis.Status)
                .SetProperty(item => item.CompletedAt, analysis.CompletedAt), cancellationToken);
        if (stored == 0)
        {
            OperationalLog.Record(logger, AuditOperation.Analysis, AuditOutcome.Discarded);
            publication.Finish(AnalysisOutcome.Discarded);
            return AnalysisOutcome.Discarded;
        }

        now = clock.GetUtcNow();
        var completed = await db.DeploymentAnalysisWorkItems.Where(item => item.AnalysisId == analysisId &&
                                                                           item.LeaseId == work.LeaseId &&
                                                                           item.CompletedAt == null &&
                                                                           item.LeaseUntil > now)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.CompletedAt, now)
                .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null), cancellationToken);
        if (completed == 0)
        {
            OperationalLog.Record(logger, AuditOperation.Analysis, AuditOutcome.Discarded);
            publication.Finish(AnalysisOutcome.Discarded);
            return
                AnalysisOutcome
                    .Discarded; // Disposing the transaction rolls back the result if ownership expired during publication.
        }

        await transaction.CommitAsync(cancellationToken);
        publication.Finish(AnalysisOutcome.Completed);
        OperationalLog.Record(logger, AuditOperation.Analysis, analysis.FailureCode switch
        {
            "invalid_response" => AuditOutcome.InvalidResult,
            "unavailable" or "unsupported_data" or "quota_exceeded" => AuditOutcome.Unavailable,
            "provider_failure" or "recovery_exhausted" or "retry_exhausted" => AuditOutcome.Failed,
            _ => analysis.Status == AiAnalysisStatus.Skipped ? AuditOutcome.Unavailable : AuditOutcome.Completed
        });
        return analysis.Status switch
        {
            AiAnalysisStatus.Completed => AnalysisOutcome.Completed,
            AiAnalysisStatus.Skipped => AnalysisOutcome.Skipped,
            _ => AnalysisOutcome.Failed
        };
    }

    /// <summary>Atomically queues a future attempt under the current lease; stores scheduling metadata only.</summary>
    private static async Task<bool> ScheduleRetryAsync(AutoMateDbContext db, TimeProvider clock,
        DeploymentAnalysisWorkItem work, TimeSpan delay, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var next = now.Add(delay);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var changed = await db.AiDeploymentAnalyses.Where(item => item.Id == work.AnalysisId &&
                                                                  item.Status == AiAnalysisStatus.Running &&
                                                                  item.ExpiresAt > next &&
                                                                  db.DeploymentAnalysisWorkItems.Any(row =>
                                                                      row.AnalysisId == work.AnalysisId &&
                                                                      row.LeaseId == work.LeaseId &&
                                                                      row.CompletedAt == null && row.LeaseUntil > now &&
                                                                      row.ProviderRetryCount ==
                                                                      work.ProviderRetryCount))
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, AiAnalysisStatus.Queued)
                .SetProperty(item => item.RetryCount, work.ProviderRetryCount + 1), token);
        if (changed == 0) return false;
        now = clock.GetUtcNow();
        changed = await db.DeploymentAnalysisWorkItems.Where(item => item.AnalysisId == work.AnalysisId &&
                                                                     item.LeaseId == work.LeaseId &&
                                                                     item.CompletedAt == null &&
                                                                     item.LeaseUntil > now &&
                                                                     item.ProviderRetryCount == work.ProviderRetryCount)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.ProviderRetryCount, work.ProviderRetryCount + 1)
                .SetProperty(item => item.NextAttemptAt, next).SetProperty(item => item.AttemptCount, 0)
                .SetProperty(item => item.LeaseId, (Guid?)null)
                .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null), token);
        if (changed == 0) return false; // Roll back the status transition if ownership changed or expired.
        await transaction.CommitAsync(token);
        return true;
    }
}

/// <summary>Fixed safe terminal outcome after the configured number of interrupted acquisitions.</summary>
internal sealed class AnalysisRecoveryExhaustedException() : Exception("Analysis recovery attempts are exhausted.");

/// <summary>Safe terminal outcome when reloaded configuration no longer permits a queued retry.</summary>
internal sealed class AnalysisRetryExhaustedException() : Exception("AI provider retry limit reached.");