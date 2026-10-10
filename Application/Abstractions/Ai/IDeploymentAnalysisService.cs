using Application.Ai;
using Domain.Enums;

namespace Application.Abstractions.Ai;

/// <summary>Owner-authorized safe result and optional provenance/usage; unknown legacy metadata remains null.</summary>
public sealed record DeploymentAnalysisView(
    Guid Id,
    Guid DeploymentId,
    AiAnalysisStatus Status,
    AiAnalysisTrigger Trigger,
    string? Summary,
    IReadOnlyList<string> RecommendedSteps,
    IReadOnlyList<string> EvidenceReferences,
    string? FailureCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? Provider = null,
    string? Model = null,
    string? RequestedModel = null,
    string? ModelVersion = null,
    string? PromptVersion = null,
    int? ResultSchemaVersion = null,
    int? InputTokens = null,
    int? OutputTokens = null,
    decimal? EstimatedCost = null,
    string? CostCurrency = null,
    AssessmentSections? Sections = null,
    AssessmentProvenance? Assessment = null);

/// <summary>
///     Safe manual admission outcome; Accepted=false may include an owner-visible terminal Skipped result without
///     queued work.
/// </summary>
public sealed record DeploymentAnalysisRequestResult(
    bool Accepted,
    string Message,
    DeploymentAnalysisView? Analysis = null,
    bool Conflict = false);

/// <summary>Deletion outcome without disclosing analyses belonging to other owners.</summary>
public enum DeploymentAnalysisDeletionResult
{
    /// <summary>The analysis and its work item were removed.</summary>
    Deleted,

    /// <summary>No matching owner-accessible analysis exists.</summary>
    NotFound,

    /// <summary>Unexpired queued/running work must finish before owner deletion.</summary>
    InProgress
}

/// <summary>Cancellation outcome without revealing analyses belonging to another owner.</summary>
public enum DeploymentAnalysisCancellationResult
{
    /// <summary>Processing was canceled, or this owner previously canceled the same analysis.</summary>
    Cancelled,

    /// <summary>No matching unexpired owner-accessible analysis exists.</summary>
    NotFound,

    /// <summary>The analysis already completed, failed or was skipped; its result is preserved.</summary>
    AlreadyFinished
}

/// <summary>Owner-authorized use case for requesting and reading deployment diagnoses.</summary>
public interface IDeploymentAnalysisService
{
    /// <summary>Requests an immutable, status-aware selection with stable idempotency.</summary>
    Task<DeploymentAnalysisRequestResult> RequestManualAsync(Guid ownerId, Guid deploymentId, Guid requestId,
        AssessmentSelection selection, CancellationToken token = default)
    {
        return RequestManualAsync(ownerId, deploymentId, requestId, token);
    }

    /// <summary>Reads owner-authorized choices and recorded channels.</summary>
    Task<AssessmentPreferences?> GetPreferencesAsync(Guid ownerId, Guid deploymentId, CancellationToken token = default)
    {
        return Task.FromResult<AssessmentPreferences?>(null);
    }

    /// <summary>Saves choices independently of running/saved analysis requests.</summary>
    Task<bool> SavePreferencesAsync(Guid ownerId, Guid deploymentId, AssessmentSelection selection,
        CancellationToken token = default)
    {
        return Task.FromResult(false);
    }

    /// <summary>Coalesces current active work; callers needing replay after completion should supply a stable request ID.</summary>
    Task<DeploymentAnalysisRequestResult> RequestManualAsync(Guid ownerId, Guid deploymentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Idempotent manual admission within a ninety-day receipt window; nonempty request IDs are owner/deployment
    ///     scoped.
    /// </summary>
    Task<DeploymentAnalysisRequestResult> RequestManualAsync(Guid ownerId, Guid deploymentId, Guid requestId,
        CancellationToken cancellationToken = default);

    /// <summary>Durably cancels unexpired queued/running work and revokes its lease; repeated cancellation is idempotent.</summary>
    Task<DeploymentAnalysisCancellationResult> CancelAsync(Guid ownerId, Guid deploymentId, Guid analysisId,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes terminal or expired analysis metadata; active work is preserved.</summary>
    Task<DeploymentAnalysisDeletionResult> DeleteAsync(Guid ownerId, Guid deploymentId, Guid analysisId,
        CancellationToken cancellationToken = default);

    Task<DeploymentAnalysisView?> GetLatestAsync(Guid ownerId, Guid deploymentId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a bounded page of all saved runs for one owned deployment, newest first.</summary>
    async Task<IReadOnlyList<DeploymentAnalysisView>> ListAsync(Guid ownerId, Guid deploymentId, int offset = 0,
        int limit = 20, CancellationToken cancellationToken = default)
    {
        var latest = await GetLatestAsync(ownerId, deploymentId, cancellationToken);
        return offset == 0 && latest is not null ? [latest] : [];
    }
}