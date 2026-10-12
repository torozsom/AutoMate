using Application.Ai;
using Domain.Enums;

namespace Application.Abstractions.Ai;

/// <summary>
///     Owner-authorized safe result and optional provenance/usage; unknown legacy metadata
///     remains null.
/// </summary>
/// <param name="Id">The record identity.</param>
/// <param name="DeploymentId">The exact deployment identity used for ownership or correlation.</param>
/// <param name="Status">The recorded analysis state.</param>
/// <param name="Trigger">
///     The manual or automatic trigger; it does not grant provider egress permission.
/// </param>
/// <param name="Summary">
///     The analysis overview; provider-authored text remains untrusted until validation.
/// </param>
/// <param name="RecommendedSteps">
///     Bounded diagnostic suggestions; these are never executed automatically.
/// </param>
/// <param name="EvidenceReferences">Exact selected identifiers cited by the analysis.</param>
/// <param name="FailureCode">The fixed failure code; null when no failure is recorded.</param>
/// <param name="CreatedAt">The UTC time at which the record was created.</param>
/// <param name="CompletedAt">The terminal completion time, if recorded.</param>
/// <param name="Provider">The provider identity; not a credential or endpoint.</param>
/// <param name="Model">The provider-returned model identity, if known.</param>
/// <param name="RequestedModel">The configured model or deployment identity used for the request.</param>
/// <param name="ModelVersion">The explicit model revision, if supplied by the provider.</param>
/// <param name="PromptVersion">
///     The AutoMate trusted prompt version used for the request, if recorded.
/// </param>
/// <param name="ResultSchemaVersion">
///     The persisted result contract version; absent legacy metadata remains unknown.
/// </param>
/// <param name="InputTokens">The provider-reported input-token count, if known.</param>
/// <param name="OutputTokens">The provider-reported output-token count, if known.</param>
/// <param name="EstimatedCost">
///     The provider-reported estimated cost, if supplied; not a billing guarantee.
/// </param>
/// <param name="CostCurrency">The uppercase three-letter currency of the estimate, if known.</param>
/// <param name="Sections">Validated version-two assessment sections, if present.</param>
/// <param name="Assessment">Recorded selection and collection provenance, if known.</param>
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
///     Safe manual admission outcome; Accepted=false may include an owner-visible terminal
///     Skipped result without queued work.
/// </summary>
/// <param name="Accepted">Whether the request was admitted for processing.</param>
/// <param name="Message">The message supplied to the operation.</param>
/// <param name="Analysis">The authorized analysis view, if admission produced or reused a result.</param>
/// <param name="Conflict">
///     Whether an existing request identity has different immutable selection options.
/// </param>
public sealed record DeploymentAnalysisRequestResult(
    bool Accepted,
    string Message,
    DeploymentAnalysisView? Analysis = null,
    bool Conflict = false);

/// <summary>
///     Deletion outcome without disclosing analyses belonging to other owners.
/// </summary>
public enum DeploymentAnalysisDeletionResult
{
    /// <summary>The analysis and its work item were removed.</summary>
    Deleted,

    /// <summary>No matching owner-accessible analysis exists.</summary>
    NotFound,

    /// <summary>Unexpired queued/running work must finish before owner deletion.</summary>
    InProgress
}

/// <summary>
///     Cancellation outcome without revealing analyses belonging to another owner.
/// </summary>
public enum DeploymentAnalysisCancellationResult
{
    /// <summary>Processing was canceled, or this owner previously canceled the same analysis.</summary>
    Cancelled,

    /// <summary>No matching unexpired owner-accessible analysis exists.</summary>
    NotFound,

    /// <summary>The analysis already completed, failed or was skipped; its result is preserved.</summary>
    AlreadyFinished
}

/// <summary>
///     Owner-authorized use case for requesting and reading deployment diagnoses.
/// </summary>
public interface IDeploymentAnalysisService
{
    /// <summary>
    ///     Requests an immutable, status-aware selection with stable idempotency.
    /// </summary>
    /// <param name="ownerId">The internal owner account identity used for authorization.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="requestId">The stable, scope-bound request identity used for idempotent admission.</param>
    /// <param name="selection">The immutable source, container, focus and time-range options.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning the accepted or declined admission outcome, including any authorized
    ///     existing or skipped result.
    /// </returns>
    /// <exception cref="ArgumentException">
    ///     The selection is invalid or its custom UTC range ends in the future.
    /// </exception>
    /// <remarks>
    ///     The compatibility implementation delegates to the legacy overload; selection-aware
    ///     adapters must override it.
    /// </remarks>
    Task<DeploymentAnalysisRequestResult> RequestManualAsync(Guid ownerId, Guid deploymentId, Guid requestId,
        AssessmentSelection selection, CancellationToken token = default)
    {
        return RequestManualAsync(ownerId, deploymentId, requestId, token);
    }

    /// <summary>
    ///     Reads owner-authorized choices and recorded channels.
    /// </summary>
    /// <param name="ownerId">The internal owner account identity used for authorization.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning the owned saved selection and source catalog, or null for missing or
    ///     unauthorized deployments.
    /// </returns>
    /// <remarks>
    ///     The compatibility implementation returns null; supporting adapters supply the owned
    ///     catalog.
    /// </remarks>
    Task<AssessmentPreferences?> GetPreferencesAsync(Guid ownerId, Guid deploymentId, CancellationToken token = default)
    {
        return Task.FromResult<AssessmentPreferences?>(null);
    }

    /// <summary>
    ///     Saves choices independently of running/saved analysis requests.
    /// </summary>
    /// <param name="ownerId">The internal owner account identity used for authorization.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="selection">The immutable source, container, focus and time-range options.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning true when choices were saved for the owned deployment; otherwise false.
    /// </returns>
    /// <exception cref="ArgumentException">
    ///     The selection is invalid or its custom UTC range ends in the future.
    /// </exception>
    /// <remarks>The compatibility implementation returns false without changing preferences.</remarks>
    Task<bool> SavePreferencesAsync(Guid ownerId, Guid deploymentId, AssessmentSelection selection,
        CancellationToken token = default)
    {
        return Task.FromResult(false);
    }

    /// <summary>
    ///     Coalesces current active work; callers needing replay after completion should supply a
    ///     stable request ID.
    /// </summary>
    /// <param name="ownerId">The internal owner account identity used for authorization.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning the accepted or declined admission outcome, including any authorized
    ///     existing or skipped result.
    /// </returns>
    Task<DeploymentAnalysisRequestResult> RequestManualAsync(Guid ownerId, Guid deploymentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Idempotent manual admission within a ninety-day receipt window; nonempty request IDs are
    ///     owner/deployment scoped.
    /// </summary>
    /// <param name="ownerId">The internal owner account identity used for authorization.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="requestId">The stable, scope-bound request identity used for idempotent admission.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning the accepted or declined admission outcome, including any authorized
    ///     existing or skipped result.
    /// </returns>
    Task<DeploymentAnalysisRequestResult> RequestManualAsync(Guid ownerId, Guid deploymentId, Guid requestId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Durably cancels unexpired queued/running work and revokes its lease; repeated
    ///     cancellation is idempotent.
    /// </summary>
    /// <param name="ownerId">The internal owner account identity used for authorization.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="analysisId">The analysis identity used for ownership or correlation.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning the fixed cancellation outcome without disclosing another owner's
    ///     analysis.
    /// </returns>
    Task<DeploymentAnalysisCancellationResult> CancelAsync(Guid ownerId, Guid deploymentId, Guid analysisId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Deletes terminal or expired analysis metadata; active work is preserved.
    /// </summary>
    /// <param name="ownerId">The internal owner account identity used for authorization.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="analysisId">The analysis identity used for ownership or correlation.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning the fixed deletion outcome; active work is preserved.</returns>
    Task<DeploymentAnalysisDeletionResult> DeleteAsync(Guid ownerId, Guid deploymentId, Guid analysisId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads the latest saved analysis for an owned deployment without invoking a provider.
    /// </summary>
    /// <param name="ownerId">The internal owner account identity used for authorization.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning the latest owned result, or null when none is available to the caller.
    /// </returns>
    Task<DeploymentAnalysisView?> GetLatestAsync(Guid ownerId, Guid deploymentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads a bounded page of all saved runs for one owned deployment, newest first.
    /// </summary>
    /// <param name="ownerId">The internal owner account identity used for authorization.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="offset">The zero-based offset of the selected page.</param>
    /// <param name="limit">The maximum number of returned or processed records.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning a bounded page of owned saved analyses, newest first.</returns>
    /// <remarks>The compatibility implementation returns only the latest result on the first page.</remarks>
    async Task<IReadOnlyList<DeploymentAnalysisView>> ListAsync(Guid ownerId, Guid deploymentId, int offset = 0,
        int limit = 20, CancellationToken cancellationToken = default)
    {
        var latest = await GetLatestAsync(ownerId, deploymentId, cancellationToken);
        return offset == 0 && latest is not null ? [latest] : [];
    }
}