using Domain.Enums;

namespace Domain.Entities;

/// <summary>Stores the status and redacted result of one requested deployment analysis.</summary>
public sealed class AiDeploymentAnalysis : BaseEntity
{
    public Guid DeploymentId { get; set; }
    public Deployment Deployment { get; set; } = null!;
    public AiAnalysisTrigger Trigger { get; set; }
    public AiAnalysisStatus Status { get; set; }

    /// <summary>Validated provider identifier, never a raw endpoint or provider payload.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Provider-returned model ID for completed results; configured model for queued/legacy results.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Configured model requested for this invocation; legacy results have no separate provenance.</summary>
    public string? RequestedModel { get; set; }

    /// <summary>Explicit provider-reported revision, when available; never inferred from an alias.</summary>
    public string? ModelVersion { get; set; }

    /// <summary>Version of AutoMate's instructions used for this invocation.</summary>
    public string? PromptVersion { get; set; }

    /// <summary>Validated result contract version; null identifies legacy, unvalidated results.</summary>
    public int? ResultSchemaVersion { get; set; }

    /// <summary>Optional provider-reported input token count.</summary>
    public int? InputTokens { get; set; }

    /// <summary>Optional provider-reported output token count.</summary>
    public int? OutputTokens { get; set; }

    /// <summary>Optional non-negative cost estimate supplied by the adapter, with eight decimal places.</summary>
    public decimal? EstimatedCost { get; set; }

    /// <summary>Three-letter uppercase currency code associated with the optional estimate.</summary>
    public string? CostCurrency { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public string? Summary { get; set; }
    public string? RecommendedStepsJson { get; set; }
    public string? EvidenceReferencesJson { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}