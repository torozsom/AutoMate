using Domain.Enums;

namespace Domain.Entities;

/// <summary>
///     Stores the status and redacted result of one requested deployment analysis.
/// </summary>
public sealed class AiDeploymentAnalysis : BaseEntity
{
    /// <summary>
    ///     Canonical immutable assessment options saved at admission; contains no diagnostic
    ///     payload.
    /// </summary>
    public string? RequestedSelectionJson { get; set; }

    /// <summary>Recorded effective selection, deployment state and evidence-window metadata.</summary>
    public string? AssessmentProvenanceJson { get; set; }

    /// <summary>Validated and redacted version-two assessment sections serialized as JSON.</summary>
    public string? AssessmentSectionsJson { get; set; }

    /// <summary>Retains saved results independently of the finite execution expiry.</summary>
    public bool RetainUntilDeleted { get; set; }

    /// <summary>Identity of the deployment being analyzed.</summary>
    public Guid DeploymentId { get; set; }

    /// <summary>Deployment associated with this analysis.</summary>
    public Deployment Deployment { get; set; } = null!;

    /// <summary>Manual or deployment-failure trigger that admitted the analysis.</summary>
    public AiAnalysisTrigger Trigger { get; set; }

    /// <summary>Durable analysis lifecycle state.</summary>
    public AiAnalysisStatus Status { get; set; }

    /// <summary>Validated provider identifier, never a raw endpoint or provider payload.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    ///     Provider-returned model ID for completed results; configured model for queued/legacy
    ///     results.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    ///     Configured model requested for this invocation; legacy results have no separate
    ///     provenance.
    /// </summary>
    public string? RequestedModel { get; set; }

    /// <summary>Explicit provider-reported revision, when available; never inferred from an alias.</summary>
    public string? ModelVersion { get; set; }

    /// <summary>Version of AutoMate's instructions used for this invocation.</summary>
    public string? PromptVersion { get; set; }

    /// <summary>Recorded result contract version; null denotes unknown legacy provenance.</summary>
    public int? ResultSchemaVersion { get; set; }

    /// <summary>Optional provider-reported input token count.</summary>
    public int? InputTokens { get; set; }

    /// <summary>Optional provider-reported output token count.</summary>
    public int? OutputTokens { get; set; }

    /// <summary>Optional non-negative cost estimate supplied by the adapter, with eight decimal places.</summary>
    public decimal? EstimatedCost { get; set; }

    /// <summary>Three-letter uppercase currency code associated with the optional estimate.</summary>
    public string? CostCurrency { get; set; }

    /// <summary>Stable admission key used to avoid duplicate analysis requests.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>Recorded number of scheduled transient provider retries.</summary>
    public int RetryCount { get; set; }

    /// <summary>Validated and redacted overview; null until a result is published.</summary>
    public string? Summary { get; set; }

    /// <summary>Validated and redacted recommendations serialized as JSON.</summary>
    public string? RecommendedStepsJson { get; set; }

    /// <summary>Exact selected evidence identifiers cited by the validated result, serialized as JSON.</summary>
    public string? EvidenceReferencesJson { get; set; }

    /// <summary>Fixed failure or skip classification; never arbitrary provider error text.</summary>
    public string? FailureCode { get; set; }

    /// <summary>Time processing started, if the request has been claimed.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>Time the analysis reached a terminal state, if recorded.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Execution eligibility deadline; saved-result retention is controlled separately.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}