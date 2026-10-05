namespace Domain.Entities;

/// <summary>Metadata-only admission receipt; quota/idempotency survive deletion or expiry of analysis results.</summary>
public sealed class AiAnalysisRequest : BaseEntity
{
    /// <summary>Project scope; only project deletion cascades this receipt.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>Owning project metadata, independent of analysis/deployment result lifetime.</summary>
    public CsProject Project { get; set; } = null!;

    /// <summary>Deployment identity retained without a cascading deployment foreign key.</summary>
    public Guid DeploymentId { get; set; }

    /// <summary>Analysis identity retained without a cascading analysis foreign key.</summary>
    public Guid AnalysisId { get; set; }

    /// <summary>Unique bounded owner/deployment/request identity; never a prompt or user-supplied free-form string.</summary>
    public string RequestKey { get; set; } = string.Empty;

    /// <summary>UTC admission day used for the project daily limit.</summary>
    public DateOnly AdmissionDay { get; set; }

    /// <summary>True for a newly admitted analysis; aliases of existing active work do not consume another allowance.</summary>
    public bool ConsumesQuota { get; set; }

    /// <summary>Ninety-day receipt expiry, independent of shorter result retention.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}