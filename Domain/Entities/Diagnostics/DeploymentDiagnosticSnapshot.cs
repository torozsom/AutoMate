using Domain.Entities.Deployments;

namespace Domain.Entities.Diagnostics;

/// <summary>
///     Legacy bounded redacted diagnostic input linked to one deployment; new context remains
///     in memory.
/// </summary>
public sealed class DeploymentDiagnosticSnapshot : BaseEntity
{
    /// <summary>Identity of the deployment associated with this legacy snapshot.</summary>
    public Guid DeploymentId { get; set; }

    /// <summary>Deployment associated with this legacy snapshot.</summary>
    public Deployment Deployment { get; set; } = null!;

    /// <summary>Retained bounded redacted legacy context, never raw provider credentials.</summary>
    public string RedactedPayload { get; set; } = string.Empty;

    /// <summary>Optional trace identity associated with the captured context.</summary>
    public string? TraceId { get; set; }

    /// <summary>Retention deadline for the legacy snapshot.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
