namespace Domain.Entities;

/// <summary>Stores a redacted, bounded diagnostic input associated with one deployment.</summary>
public sealed class DeploymentDiagnosticSnapshot : BaseEntity
{
    public Guid DeploymentId { get; set; }
    public Deployment Deployment { get; set; } = null!;
    public string RedactedPayload { get; set; } = string.Empty;
    public string? TraceId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
