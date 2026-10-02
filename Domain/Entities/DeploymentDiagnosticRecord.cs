namespace Domain.Entities;

/// <summary>Redacted normalized diagnostic retained for terminal replay and bounded AI context.</summary>
public sealed class DeploymentDiagnosticRecord : BaseEntity
{
    public long OrderId { get; set; }
    public Guid? DeploymentId { get; set; }
    public Deployment? Deployment { get; set; }
    public Guid ProjectId { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? TerminalChannel { get; set; }
    public string? AttributesJson { get; set; }
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }
    public long? Sequence { get; set; }
    public string? Cursor { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}