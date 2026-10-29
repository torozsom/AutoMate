using System.Text.Json;
using Application.Abstractions.Ai.Context;
using Application.Abstractions.Ai.Execution;
using Application.Abstractions.Ai.Policy;
using Application.Abstractions.Ai.Results;
using Application.Ai.Context;
using Application.Ai.Policy;
using Application.Ai.Results;
using Application.Diagnostics;
using Domain.Enums.Ai;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Ai.Processing;

/// <summary>
///     Executes one leased analysis with explicit dependencies; deferred boundaries preserve admission-before-provider
///     ordering.
/// </summary>
/// <param name="db">The operation-owned metadata context.</param>
/// <param name="clock">Samples the same execution and lease decision times as the worker.</param>
/// <param name="logger">Retains the existing worker logging category and safe audit contract.</param>
/// <param name="settings">Reads reloadable policy only when the execution reaches a policy check.</param>
/// <param name="authorizer">Rechecks current egress authorization.</param>
/// <param name="contextBuilder">Acquires bounded evidence after authorization.</param>
/// <param name="budget">Reserves attempt cost before any provider construction.</param>
/// <param name="provider">Constructs the provider only after all admission and lease gates pass.</param>
/// <param name="validator">Validates the complete provider response before persistence.</param>
internal sealed class DeploymentAnalysisProcessor(
    AutoMateDbContext db,
    TimeProvider clock,
    ILogger<DeploymentAnalysisWorker> logger,
    Lazy<IOptionsMonitor<AiAnalysisOptions>> settings,
    Lazy<IAnalysisEgressAuthorizer> authorizer,
    Lazy<IDeploymentAnalysisContextBuilder> contextBuilder,
    Lazy<IAnalysisBudgetGuard> budget,
    Lazy<ILlmAnalysisProvider> provider,
    Lazy<IAnalysisResultValidator> validator)
{
    /// <summary>
    ///     Returns only the fenced, committed outcome inside the worker-owned processing telemetry.
    /// </summary>
    /// <param name="work">The detached claim and its current lease ownership.</param>
    /// <param name="token">Cancels processing without suppressing host-owned release.</param>
    /// <returns>The observed asynchronous processing operation.</returns>
    internal Task<AnalysisOutcome> ProcessAsync(DeploymentAnalysisWorkItem work, CancellationToken token)
    {
        return ProcessCoreAsync(work, token);
    }

    /// <summary>
    ///     Returns only the committed outcome; stale writes and retries cannot report a completed
    ///     analysis.
    /// </summary>
    /// <param name="work">The detached analysis claim and its lease ownership token.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning the analysis outcome projection derived from the verified inputs.</returns>
    private async Task<AnalysisOutcome> ProcessCoreAsync(
        DeploymentAnalysisWorkItem work,
        CancellationToken cancellationToken)
    {
        if (work.LeaseId == Guid.Empty) return AnalysisOutcome.Discarded;

        var analysisId = work.AnalysisId;
        var startedAt = clock.GetUtcNow();

        var analysis = await db.AiDeploymentAnalyses.AsNoTracking().FirstOrDefaultAsync(
            item => item.Id == analysisId && item.ExpiresAt > startedAt, cancellationToken);

        if (analysis is null) return AnalysisOutcome.Discarded;

        using var correlation = OperationalLog.BeginCorrelation(logger, analysis.DeploymentId, analysisId);

        OperationalLog.Record(logger, AuditOperation.Analysis, AuditOutcome.Started);
        analysis.Status = AiAnalysisStatus.Running;
        analysis.StartedAt = clock.GetUtcNow();

        // Conditional writes prevent expired/deleted work from starting a late analysis.
        if (await db.AiDeploymentAnalyses
                .Where(item =>
                    item.Id == analysisId
                    && item.ExpiresAt > startedAt
                    && (item.Status == AiAnalysisStatus.Queued ||
                        item.Status == AiAnalysisStatus.Running)
                    && db.DeploymentAnalysisWorkItems
                        .Any(row =>
                            row.AnalysisId == analysisId
                            && row.LeaseId == work.LeaseId
                            && row.CompletedAt == null && row.LeaseUntil > startedAt))
                .ExecuteUpdateAsync(update =>
                    update.SetProperty(item => item.Status, AiAnalysisStatus.Running)
                        .SetProperty(item => item.StartedAt, startedAt)
                        .SetProperty(item => item.RetryCount, work.ProviderRetryCount), cancellationToken) ==
            0) return AnalysisOutcome.Discarded;

        var providerStarted = false;

        try
        {
            if (work.Attempt > Math.Clamp(settings.Value.CurrentValue.MaximumRecoveryAttempts, 1, 10))
                throw new AnalysisRecoveryExhaustedException();

            if (work.ProviderRetryCount > Math.Clamp(settings.Value.CurrentValue.MaximumProviderRetries, 0, 5))
                throw new AnalysisRetryExhaustedException();

            var egress = authorizer.Value;

            if (!await egress.AuthorizeAsync(analysis.DeploymentId, analysis.Trigger, cancellationToken))
                throw new AnalysisProviderUnavailableException();

            var context = await AnalysisTelemetry.RunAsync(AnalysisOperation.Context, analysis.DeploymentId, analysisId,
                () => contextBuilder.Value
                    .BuildAsync(analysis.DeploymentId,
                        AssessmentSelection.Read(analysis.RequestedSelectionJson), cancellationToken),
                result => string.IsNullOrWhiteSpace(result.Text)
                    ? AnalysisOutcome.Empty
                    : AnalysisOutcome.Completed,
                cancellationToken);

            analysis.AssessmentProvenanceJson =
                context.Provenance is null ? null : JsonSerializer.Serialize(context.Provenance);

            if (string.IsNullOrWhiteSpace(context.Text))
                throw new AnalysisProviderUnavailableException(AnalysisSkipReason.UnsupportedData);

            var evidence = Array.AsReadOnly([.. context.EvidenceReferences]);
            var contextCheckedAt = clock.GetUtcNow();

            if (!await db.AiDeploymentAnalyses
                    .AsNoTracking()
                    .AnyAsync(item => item.Id == analysisId
                                      && item.ExpiresAt > contextCheckedAt
                                      && db.DeploymentAnalysisWorkItems
                                          .Any(row =>
                                              row.AnalysisId == analysisId
                                              && row.LeaseId == work.LeaseId
                                              && row.CompletedAt == null
                                              && row.LeaseUntil > contextCheckedAt),
                        cancellationToken)
               )
                return AnalysisOutcome.Discarded;

            if (!await egress.AuthorizeAsync(analysis.DeploymentId, analysis.Trigger, cancellationToken))
                throw new AnalysisProviderUnavailableException();

            var spendSettings = settings.Value.CurrentValue;
            var denied = await budget.Value.ReserveAttemptAsync(work, cancellationToken);

            if (!ReferenceEquals(spendSettings,
                    settings.Value.CurrentValue))
                denied = AnalysisSkipReason.Unavailable;

            if (denied is { } reason)
            {
                OperationalLog.Record(logger, AuditOperation.Analysis, AuditOutcome.Denied);
                throw new AnalysisProviderUnavailableException(reason);
            }

            var providerCheckedAt = clock.GetUtcNow();

            if (!await db.DeploymentAnalysisWorkItems
                    .AsNoTracking()
                    .AnyAsync(item =>
                            item.AnalysisId == analysisId
                            && item.LeaseId == work.LeaseId
                            && item.CompletedAt == null
                            && item.LeaseUntil > providerCheckedAt
                            && item.Analysis.ExpiresAt > providerCheckedAt
                            && item.Analysis.Status == AiAnalysisStatus.Running,
                        cancellationToken)) return AnalysisOutcome.Discarded;

            providerStarted = true;
            OperationalLog.Record(logger, AuditOperation.AnalysisProvider, AuditOutcome.Started);
            var response = await AnalysisTelemetry.RunAsync(AnalysisOperation.Provider, analysis.DeploymentId,
                analysisId, async () =>
                {
                    var result = await provider.Value
                        .AnalyzeAsync(
                            new LlmAnalysisRequest(context.Text, evidence, analysis.DeploymentId, analysis.Trigger,
                                context.Provenance?.EffectiveKind ?? AssessmentKind.Automatic),
                            cancellationToken);

                    result = validator.Value.Validate(result);
                    result = AnalysisEvidence.Validate(result, evidence);
                    AnalysisTelemetry.Usage(result);

                    return result;
                }, token: cancellationToken);

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
                settings.Value.CurrentValue,
                work.ProviderRetryCount, error.RetryAfterSeconds);

            if (delay is { } wait && clock.GetUtcNow().Add(wait) < analysis.ExpiresAt)
            {
                var scheduled = await AnalysisTelemetry.RunAsync(AnalysisOperation.Retry, analysis.DeploymentId,
                    analysisId, () => ScheduleRetryAsync(db, clock, work, wait, cancellationToken),
                    result => result ? AnalysisOutcome.RetryScheduled : AnalysisOutcome.Discarded, cancellationToken);

                OperationalLog.Record(logger, AuditOperation.Analysis,
                    scheduled ? AuditOutcome.RetryScheduled : AuditOutcome.Discarded);

                return scheduled ? AnalysisOutcome.RetryScheduled : AnalysisOutcome.Discarded;
            }

            analysis.Status = AiAnalysisStatus.Failed;
            analysis.FailureCode = "retry_exhausted";
            analysis.CompletedAt = clock.GetUtcNow();
        }
        catch (AnalysisProviderAccessDeniedException error)
        {
            logger.LogError(error, "AI analysis execution failed: {FailureType}.", error.GetType().Name);
            if (providerStarted) OperationalLog.Record(logger, AuditOperation.AnalysisProvider, AuditOutcome.Failed);
            analysis.Status = AiAnalysisStatus.Failed;
            analysis.FailureCode = AnalysisProviderAccessDeniedException.FailureCode;
            analysis.Summary = AnalysisProviderAccessDeniedException.Guidance;
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
        catch (InvalidAnalysisResultException rejection)
        {
            logger.LogWarning("AI result rejected: {FailureCode}.", rejection.FailureCode);
            if (providerStarted)
                OperationalLog.Record(logger, AuditOperation.AnalysisProvider, AuditOutcome.InvalidResult);
            analysis.Status = AiAnalysisStatus.Failed;
            analysis.FailureCode = rejection.FailureCode;
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
        using var publication =
            AnalysisTelemetry.Start(AnalysisOperation.Publish, analysis.DeploymentId, analysisId, cancellationToken);

        var publicationCheckedAt = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Update the analysis record only if it is still running and the work item is still valid.
        var stored = await db.AiDeploymentAnalyses
            .Where(item =>
                item.Id == analysisId
                && item.ExpiresAt > publicationCheckedAt
                && item.Status == AiAnalysisStatus.Running
                && db.DeploymentAnalysisWorkItems
                    .Any(row =>
                        row.AnalysisId == analysisId
                        && row.LeaseId == work.LeaseId
                        && row.CompletedAt == null
                        && row.LeaseUntil > publicationCheckedAt))
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

        var completedAt = clock.GetUtcNow();

        // Update the work item record only if it is still valid.
        var completed = await db.DeploymentAnalysisWorkItems
            .Where(item => item.AnalysisId == analysisId
                           && item.LeaseId == work.LeaseId
                           && item.CompletedAt == null
                           && item.LeaseUntil > completedAt)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.CompletedAt, completedAt)
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
            var code when code?.StartsWith("invalid_response", StringComparison.Ordinal) == true => AuditOutcome
                .InvalidResult,
            "unavailable" or "unsupported_data" or "quota_exceeded" => AuditOutcome.Unavailable,
            "provider_failure" or "provider_access_denied" or "recovery_exhausted" or "retry_exhausted" => AuditOutcome
                .Failed,
            _ => analysis.Status == AiAnalysisStatus.Skipped ? AuditOutcome.Unavailable : AuditOutcome.Completed
        });
        return analysis.Status switch
        {
            AiAnalysisStatus.Completed => AnalysisOutcome.Completed,
            AiAnalysisStatus.Skipped => AnalysisOutcome.Skipped,
            _ => AnalysisOutcome.Failed
        };
    }

    /// <summary>
    ///     Atomically queues a future attempt under the current lease; stores scheduling metadata
    ///     only.
    /// </summary>
    /// <param name="db">Application metadata context used for ownership, leases and persisted state.</param>
    /// <param name="clock">Time source used for deterministic sampling and deadlines.</param>
    /// <param name="work">The detached analysis claim and its lease ownership token.</param>
    /// <param name="delay">Bounded wait before the next scheduled attempt.</param>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>A task returning whether the documented schedule retry async condition holds.</returns>
    private static async Task<bool> ScheduleRetryAsync(AutoMateDbContext db, TimeProvider clock,
        DeploymentAnalysisWorkItem work, TimeSpan delay, CancellationToken token)
    {
        var scheduledAt = clock.GetUtcNow();
        var next = scheduledAt.Add(delay);

        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var changed = await db.AiDeploymentAnalyses
            .Where(item =>
                item.Id == work.AnalysisId
                && item.Status == AiAnalysisStatus.Running
                && item.ExpiresAt > next
                && db.DeploymentAnalysisWorkItems
                    .Any(row =>
                        row.AnalysisId == work.AnalysisId
                        && row.LeaseId == work.LeaseId
                        && row.CompletedAt == null
                        && row.LeaseUntil > scheduledAt
                        && row.ProviderRetryCount == work.ProviderRetryCount))
            .ExecuteUpdateAsync(update =>
                update.SetProperty(item => item.Status, AiAnalysisStatus.Queued)
                    .SetProperty(item => item.RetryCount, work.ProviderRetryCount + 1), token);

        if (changed == 0) return false;
        var retryCheckedAt = clock.GetUtcNow();

        changed = await db.DeploymentAnalysisWorkItems
            .Where(item => item.AnalysisId == work.AnalysisId
                           && item.LeaseId == work.LeaseId
                           && item.CompletedAt == null
                           && item.LeaseUntil > retryCheckedAt
                           && item.ProviderRetryCount == work.ProviderRetryCount)
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
