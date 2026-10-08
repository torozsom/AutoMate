namespace Application.Abstractions.Diagnostics;

/// <summary>Private gateway request for bounded archived metric aggregation.</summary>
public sealed record ArchiveMetricRequest(
    Guid Tenant,
    Guid Project,
    Guid Deployment,
    DateTimeOffset Start,
    DateTimeOffset End,
    int MaximumPoints);

/// <summary>Private cleanup request allowed only for a committed deletion-outbox item.</summary>
public sealed record ArchiveDeleteRequest(Guid Tenant, Guid Project);

/// <summary>Retained backend interval statistics; these are not raw sample counts.</summary>
public sealed record ArchiveMetricImport(
    Guid Tenant,
    Guid Project,
    Guid Deployment,
    IReadOnlyList<DeploymentMetricPoint> Points);

/// <summary>Permanent redacted deployment history, separate from the operational telemetry retention window.</summary>
public interface IDeploymentArchive
{
    /// <summary>Durably appends an immutable event; duplicate identities return the original receipt.</summary>
    Task<DeploymentLogEnvelope> AppendAsync(DeploymentLogEnvelope envelope, CancellationToken token);

    /// <summary>Returns a bounded ordered log page with optional plain-text search.</summary>
    Task<IReadOnlyList<DeploymentLogEnvelope>> ReadAsync(Guid tenant, Guid project, Guid deployment,
        long cursor, bool backwards, int limit, string? search, CancellationToken token);

    /// <summary>Aggregates retained numeric samples without zero-filling missing intervals.</summary>
    Task<IReadOnlyList<DeploymentMetricPoint>> ReadMetricsAsync(Guid tenant, Guid project, Guid deployment,
        DateTimeOffset start, DateTimeOffset end, int maximumPoints, CancellationToken token);

    /// <summary>Removes one deleted project's partition; repeated cleanup is harmless.</summary>
    Task DeleteProjectAsync(Guid tenant, Guid project, CancellationToken token);

    /// <summary>Idempotently imports still-available interval statistics from the operational backend.</summary>
    Task ImportMetricsAsync(ArchiveMetricImport import, CancellationToken token);
}