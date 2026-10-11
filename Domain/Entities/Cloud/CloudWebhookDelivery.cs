namespace Domain.Entities;

/// <summary>Minimal verified webhook receipt used for replay protection and async processing.</summary>
public sealed class CloudWebhookDelivery : BaseEntity
{
    /// <summary>GitHub delivery identifier, stable across redeliveries.</summary>
    public string DeliveryId { get; set; } = string.Empty;

    /// <summary>GitHub App installation identifier.</summary>
    public long InstallationId { get; set; }

    /// <summary>Numeric repository identifier.</summary>
    public long RepositoryId { get; set; }

    /// <summary>Workflow run identifier.</summary>
    public long WorkflowRunId { get; set; }

    /// <summary>Workflow attempt identifier.</summary>
    public int WorkflowAttempt { get; set; }

    /// <summary>Head commit SHA.</summary>
    public string HeadSha { get; set; } = string.Empty;

    /// <summary>Branch reported for this workflow run.</summary>
    public string HeadBranch { get; set; } = string.Empty;

    /// <summary>Workflow path used to reject unrelated Actions runs.</summary>
    public string WorkflowPath { get; set; } = string.Empty;

    /// <summary>Normalized GitHub run status.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Normalized GitHub conclusion, when complete.</summary>
    public string? Conclusion { get; set; }

    /// <summary>Time the receipt was applied to a run.</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Worker currently processing this receipt.</summary>
    public Guid? LeaseOwner { get; set; }

    /// <summary>Time after which a failed receipt claim can be recovered.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }
}