using Domain.Entities.Projects;

namespace Domain.Entities.Ai;

/// <summary>
///     Metadata-only admission receipt; quota/idempotency survive deletion or expiry of
///     analysis results.
/// </summary>
public sealed class AiAnalysisRequest : BaseEntity
{
    /// <summary>Canonical request options survive result deletion and prevent conflicting replay.</summary>
    public string? RequestedSelectionJson { get; init; }

    /// <summary>Project scope; only project deletion cascades this receipt.</summary>
    public Guid ProjectId { get; init; }

    /// <summary>Owning project metadata, independent of analysis/deployment result lifetime.</summary>
    public CsProject Project { get; init; } = null!;

    /// <summary>Deployment identity retained without a cascading deployment foreign key.</summary>
    public Guid DeploymentId { get; init; }

    /// <summary>Analysis identity retained without a cascading analysis foreign key.</summary>
    public Guid AnalysisId { get; init; }

    /// <summary>
    ///     Unique bounded owner/deployment/request identity; never a prompt or user-supplied free-
    ///     form string.
    /// </summary>
    public string RequestKey { get; init; } = string.Empty;

    /// <summary>UTC admission day used for the project daily limit.</summary>
    public DateOnly AdmissionDay { get; init; }

    /// <summary>
    ///     True for a newly admitted analysis; aliases of existing active work do not consume
    ///     another allowance.
    /// </summary>
    public bool ConsumesQuota { get; init; }

    /// <summary>Receipt expiry, independent of execution eligibility and saved-result retention.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}
