using System.Data;
using System.Text.Json;
using Application.Abstractions.Ai;
using Application.Ai;
using Application.Diagnostics;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DeploymentAnalysisWorkItem = Domain.Entities.DeploymentAnalysisWorkItem;

namespace Infrastructure.Ai;

/// <summary>Owner-authorized analysis admission and redacted readback, including legacy-result validation.</summary>
public sealed class DeploymentAnalysisService(
    AutoMateDbContext dbContext,
    IOptionsMonitor<AiAnalysisOptions> options,
    IAnalysisResultValidator validator,
    TimeProvider clock,
    IAnalysisEgressAuthorizer egress,
    ILogger<DeploymentAnalysisService> logger)
    : IDeploymentAnalysisService
{
    /// <summary>Bounds receipt aliases per UTC project day independently of the smaller admitted-analysis quota.</summary>
    internal const int MaximumDailyRequestReceipts = 1_000;

    /// <inheritdoc />
    public Task<DeploymentAnalysisRequestResult> RequestManualAsync(Guid ownerId, Guid deploymentId,
        CancellationToken cancellationToken = default)
    {
        return AdmitAsync(ownerId, deploymentId, null, AiAnalysisTrigger.Manual, cancellationToken);
    }

    /// <inheritdoc />
    public Task<DeploymentAnalysisRequestResult> RequestManualAsync(Guid ownerId, Guid deploymentId, Guid requestId,
        CancellationToken cancellationToken = default)
    {
        return AdmitAsync(ownerId, deploymentId, requestId, AiAnalysisTrigger.Manual, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DeploymentAnalysisView?> GetLatestAsync(Guid ownerId, Guid deploymentId,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var analysis = await dbContext.AiDeploymentAnalyses.AsNoTracking()
            .Where(item =>
                item.DeploymentId == deploymentId && item.ExpiresAt > now &&
                item.Deployment.CsProject!.Application!.UserId == ownerId)
            .OrderByDescending(item => item.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        return analysis is null ? null : ToView(analysis);
    }

    /// <inheritdoc />
    public async Task<DeploymentAnalysisDeletionResult> DeleteAsync(Guid ownerId, Guid deploymentId, Guid analysisId,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var owned = dbContext.AiDeploymentAnalyses.Where(item => item.Id == analysisId &&
                                                                 item.DeploymentId == deploymentId &&
                                                                 item.Deployment.CsProject!.Application!.UserId ==
                                                                 ownerId);
        // Authorization and state are predicates of the deletion itself, including races with workers.
        var removed = await owned.Where(item => item.ExpiresAt <= now ||
                                                (item.Status != AiAnalysisStatus.Queued &&
                                                 item.Status != AiAnalysisStatus.Running))
            .ExecuteDeleteAsync(cancellationToken);
        if (removed > 0) return DeploymentAnalysisDeletionResult.Deleted;
        return await owned.AnyAsync(cancellationToken)
            ? DeploymentAnalysisDeletionResult.InProgress
            : DeploymentAnalysisDeletionResult.NotFound;
    }

    /// <inheritdoc />
    public async Task<DeploymentAnalysisCancellationResult> CancelAsync(Guid ownerId, Guid deploymentId,
        Guid analysisId,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var owned = dbContext.AiDeploymentAnalyses.Where(item =>
            item.Id == analysisId && item.DeploymentId == deploymentId &&
            item.ExpiresAt > now && item.Deployment.CsProject!.Application!.UserId == ownerId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        // Ownership and active state are predicates of the write, including races with worker publication/requeue.
        var changed = await owned
            .Where(item => item.Status == AiAnalysisStatus.Queued || item.Status == AiAnalysisStatus.Running)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, AiAnalysisStatus.Cancelled)
                .SetProperty(item => item.CompletedAt, now).SetProperty(item => item.Summary, (string?)null)
                .SetProperty(item => item.RecommendedStepsJson, (string?)null)
                .SetProperty(item => item.EvidenceReferencesJson, (string?)null)
                .SetProperty(item => item.FailureCode, (string?)null), cancellationToken);
        if (changed == 0)
        {
            var status = await owned.Select(item => (AiAnalysisStatus?)item.Status)
                .FirstOrDefaultAsync(cancellationToken);
            return status switch
            {
                null => DeploymentAnalysisCancellationResult.NotFound,
                AiAnalysisStatus.Cancelled => DeploymentAnalysisCancellationResult.Cancelled,
                _ => DeploymentAnalysisCancellationResult.AlreadyFinished
            };
        }

        await dbContext.DeploymentAnalysisWorkItems
            .Where(item => item.AnalysisId == analysisId && item.CompletedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.CompletedAt, now)
                .SetProperty(item => item.LeaseId, (Guid?)null)
                .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(item => item.NextAttemptAt, (DateTimeOffset?)null), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        using var correlation = OperationalLog.BeginCorrelation(logger, deploymentId, analysisId);
        OperationalLog.Record(logger, AuditOperation.Analysis, AuditOutcome.CanceledByOwner);
        return DeploymentAnalysisCancellationResult.Cancelled;
    }

    /// <summary>Consumes a persisted failure wakeup through the same project guard and quota ledger as manual requests.</summary>
    public async Task<DeploymentAnalysisRequestResult> RequestAutomaticAsync(Guid deploymentId,
        CancellationToken token = default)
    {
        var ownerId = await dbContext.Deployments.AsNoTracking().Where(item => item.Id == deploymentId)
            .Select(item => (Guid?)item.CsProject!.Application.UserId).FirstOrDefaultAsync(token);
        return ownerId is { } owner
            ? await AdmitAsync(owner, deploymentId, deploymentId, AiAnalysisTrigger.DeploymentFailed, token)
            : new DeploymentAnalysisRequestResult(false, "Deployment not found.");
    }

    /// <summary>Serializes short admission transactions per project; no context/provider call occurs while the guard is held.</summary>
    private async Task<DeploymentAnalysisRequestResult> AdmitAsync(Guid ownerId, Guid deploymentId, Guid? requestId,
        AiAnalysisTrigger trigger, CancellationToken token)
    {
        if (requestId == Guid.Empty)
            return new DeploymentAnalysisRequestResult(false, "A nonempty analysis request ID is required.");
        var settings = options.CurrentValue;
        if (!settings.Enabled)
            return new DeploymentAnalysisRequestResult(false, "AI analysis is disabled by the operator.");
        if (trigger == AiAnalysisTrigger.DeploymentFailed && !settings.AutomaticAnalysisEnabled)
            return new DeploymentAnalysisRequestResult(false, "Automatic AI analysis is disabled by the operator.");
        var scope = await dbContext.Deployments.AsNoTracking().Where(item => item.Id == deploymentId &&
                                                                             item.CsProject!.Application.UserId ==
                                                                             ownerId)
            .Select(item => new { item.CsProjectId }).FirstOrDefaultAsync(token);
        if (scope is null) return new DeploymentAnalysisRequestResult(false, "Deployment not found.");
        await using var transaction = dbContext.Database.IsNpgsql()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token)
            : await dbContext.Database.BeginTransactionAsync(token);
        // The no-op UPDATE obtains a database row lock without changing project configuration or audit timestamps.
        // All admissions for this project observe committed receipts after acquiring the same guard (Read Committed).
        if (await dbContext.CsProjects.Where(item => item.Id == scope.CsProjectId && item.Application.UserId == ownerId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Name, item => item.Name), token) == 0)
            return new DeploymentAnalysisRequestResult(false, "Deployment not found.");
        var failure = trigger == AiAnalysisTrigger.DeploymentFailed
            ? await dbContext.FailedDeploymentAnalysisEvents.AsNoTracking()
                .SingleOrDefaultAsync(item => item.DeploymentId == deploymentId, token)
            : null;
        if (trigger == AiAnalysisTrigger.DeploymentFailed && (failure is null || failure.CompletedAt is not null))
            return new DeploymentAnalysisRequestResult(false,
                "This deployment failure was already processed or is not pending.");
        var deployment = await dbContext.Deployments.AsNoTracking().Where(item => item.Id == deploymentId &&
                item.CsProjectId == scope.CsProjectId && item.CsProject!.Application.UserId == ownerId)
            .Select(item => new
            {
                item.Status,
                Consent = item.CsProject!.Configuration != null &&
                          item.CsProject.Configuration.AiDiagnosticEgressConsented
            })
            .FirstOrDefaultAsync(token);
        if (deployment is null) return new DeploymentAnalysisRequestResult(false, "Deployment not found.");
        if (trigger == AiAnalysisTrigger.DeploymentFailed && deployment.Status != DeploymentStatus.Failed)
            return new DeploymentAnalysisRequestResult(false, "The deployment is no longer failed.");
        if (!deployment.Consent)
            return new DeploymentAnalysisRequestResult(false,
                "Enable AI diagnostic data egress in project configuration before requesting analysis.");
        var approved = await egress.AuthorizeAsync(deploymentId, trigger, token) &&
                       ReferenceEquals(settings, options.CurrentValue);
        var now = clock.GetUtcNow();
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        var key = trigger == AiAnalysisTrigger.DeploymentFailed
            ? $"automatic:{deploymentId:N}"
            : $"manual:{ownerId:N}:{deploymentId:N}:{requestId ?? Guid.NewGuid():N}";
        var receipt = await dbContext.AiAnalysisRequests.AsNoTracking()
            .FirstOrDefaultAsync(item => item.RequestKey == key, token);
        if (receipt is not null)
        {
            var prior = await dbContext.AiDeploymentAnalyses.AsNoTracking().FirstOrDefaultAsync(item =>
                item.Id == receipt.AnalysisId &&
                item.DeploymentId == deploymentId && item.ExpiresAt > now &&
                item.Deployment.CsProject!.Application.UserId == ownerId, token);
            return receipt.ExpiresAt > now && prior is not null
                ? new DeploymentAnalysisRequestResult(approved && prior.Status != AiAnalysisStatus.Skipped,
                    prior.Status == AiAnalysisStatus.Skipped
                        ? AnalysisSkipPolicy.Message(prior.FailureCode)
                        : "This analysis request was already accepted.", ToView(prior))
                : new DeploymentAnalysisRequestResult(false,
                    "This request was already processed and its result is no longer available.");
        }

        var existing = !approved
            ? null
            : await dbContext.AiDeploymentAnalyses.AsNoTracking().Where(item => item.DeploymentId == deploymentId &&
                    item.ExpiresAt > now &&
                    (item.Status == AiAnalysisStatus.Queued || item.Status == AiAnalysisStatus.Running))
                .OrderByDescending(item => item.CreatedAt).FirstOrDefaultAsync(token);
        if (existing is not null && requestId is null)
            return new DeploymentAnalysisRequestResult(true, "An analysis is already in progress.", ToView(existing));
        if (await dbContext.AiAnalysisRequests.CountAsync(
                item => item.ProjectId == scope.CsProjectId && item.AdmissionDay == day, token)
            >= MaximumDailyRequestReceipts)
            return new DeploymentAnalysisRequestResult(false,
                "The daily analysis request limit for this project has been reached.");
        AnalysisSkipReason? skip = approved ? null : AnalysisSkipReason.Unavailable;
        if (skip is null && existing is null && await dbContext.AiAnalysisRequests.CountAsync(item =>
                item.ProjectId == scope.CsProjectId &&
                item.AdmissionDay == day && item.ConsumesQuota, token) >=
            Math.Clamp(settings.DailyProjectLimit, 0, 1_000))
            skip = AnalysisSkipReason.ProjectQuotaExceeded;
        if (failure is not null && await dbContext.FailedDeploymentAnalysisEvents
                .Where(item => item.DeploymentId == deploymentId && item.CompletedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.CompletedAt, now), token) == 0)
            return new DeploymentAnalysisRequestResult(false, "This deployment failure was already processed.");
        var analysis = existing ?? new AiDeploymentAnalysis
        {
            DeploymentId = deploymentId,
            Trigger = trigger,
            Status = skip is null ? AiAnalysisStatus.Queued : AiAnalysisStatus.Skipped,
            Provider = skip is null ? settings.Provider ?? "unavailable" : "unavailable",
            Model = skip is null ? settings.Model : "unavailable",
            FailureCode = skip is { } reason ? AnalysisSkipPolicy.Code(reason) : null,
            Summary = skip is { } skipped ? AnalysisSkipPolicy.Message(AnalysisSkipPolicy.Code(skipped)) : null,
            CompletedAt = skip is null ? null : now,
            IdempotencyKey = key,
            ExpiresAt = now.AddDays(Math.Clamp(settings.ResultRetentionDays, 1, 90))
        };
        var admitted = existing is null;
        var work = admitted && skip is null ? new DeploymentAnalysisWorkItem { Analysis = analysis } : null;
        if (admitted) dbContext.AiDeploymentAnalyses.Add(analysis);
        if (work is not null) dbContext.DeploymentAnalysisWorkItems.Add(work);
        var newReceipt = new AiAnalysisRequest
        {
            ProjectId = scope.CsProjectId,
            DeploymentId = deploymentId,
            AnalysisId = analysis.Id,
            RequestKey = key,
            AdmissionDay = day,
            ConsumesQuota = work is not null,
            ExpiresAt = now.AddDays(90)
        };
        dbContext.AiAnalysisRequests.Add(newReceipt);
        try
        {
            await dbContext.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        catch
        {
            // Remove only this admission's entities so a reused scoped context cannot later save a failed request.
            dbContext.Entry(newReceipt).State = EntityState.Detached;
            if (work is not null) dbContext.Entry(work).State = EntityState.Detached;
            if (admitted) dbContext.Entry(analysis).State = EntityState.Detached;
            throw;
        }

        return new DeploymentAnalysisRequestResult(skip is null, skip is null
            ? admitted ? "Analysis queued." : "An analysis is already in progress."
            : AnalysisSkipPolicy.Message(analysis.FailureCode), ToView(analysis));
    }

    /// <summary>Returns authorized result provenance and optional usage without exposing provider payloads.</summary>
    private DeploymentAnalysisView ToView(AiDeploymentAnalysis item)
    {
        if (item.Status == AiAnalysisStatus.Cancelled)
            return new DeploymentAnalysisView(item.Id, item.DeploymentId, item.Status, item.Trigger,
                "AI analysis was canceled by its owner.", [], [], null, item.CreatedAt, item.CompletedAt);
        if (item.Status == AiAnalysisStatus.Skipped)
            return new DeploymentAnalysisView(item.Id, item.DeploymentId, item.Status, item.Trigger,
                AnalysisSkipPolicy.Message(item.FailureCode), [], [],
                item.FailureCode is "unavailable" or "quota_exceeded" or "unsupported_data"
                    ? item.FailureCode
                    : "unavailable",
                item.CreatedAt, item.CompletedAt);
        try
        {
            var completed = item.Status == AiAnalysisStatus.Completed;
            var safe = validator.Validate(new LlmAnalysisResponse(item.Provider, item.Model,
                completed ? item.Summary ?? string.Empty : "Analysis result unavailable.",
                completed ? Deserialize(item.RecommendedStepsJson) : [],
                completed ? Deserialize(item.EvidenceReferencesJson) : [],
                item.InputTokens, item.OutputTokens, item.RequestedModel, item.ModelVersion, item.PromptVersion,
                item.ResultSchemaVersion, item.EstimatedCost, item.CostCurrency));
            return new DeploymentAnalysisView(item.Id, item.DeploymentId, item.Status,
                item.Trigger, completed
                    ? safe.Summary
                    : item.Status == AiAnalysisStatus.Skipped
                        ? "AI analysis is unavailable or no diagnostic data was found."
                        : item.FailureCode == "recovery_exhausted"
                            ? "AI analysis stopped after repeated worker interruptions."
                            : null,
                safe.RecommendedSteps, safe.EvidenceReferences,
                item.FailureCode is "unavailable" or "invalid_response" or "provider_failure" or "recovery_exhausted"
                    or "retry_exhausted"
                    ? item.FailureCode
                    : null,
                item.CreatedAt, item.CompletedAt, safe.Provider, safe.Model, safe.RequestedModel,
                safe.ModelVersion, safe.PromptVersion, item.ResultSchemaVersion, safe.InputTokens, safe.OutputTokens,
                safe.EstimatedCost, safe.CostCurrency);
        }
        catch (Exception exception) when (exception is InvalidAnalysisResultException or JsonException)
        {
            return new DeploymentAnalysisView(item.Id, item.DeploymentId, AiAnalysisStatus.Failed, item.Trigger,
                null, [], [], "invalid_response", item.CreatedAt, item.CompletedAt);
        }
    }

    /// <summary>Bounds legacy JSON before parsing; corrupt saved results become safe unavailable views.</summary>
    private static IReadOnlyList<string> Deserialize(string? value)
    {
        if (value?.Length > 131_072) throw new InvalidAnalysisResultException();
        return string.IsNullOrWhiteSpace(value) ? [] : JsonSerializer.Deserialize<string[]>(value) ?? [];
    }
}