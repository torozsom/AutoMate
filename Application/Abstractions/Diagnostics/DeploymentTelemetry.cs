using Application.Abstractions.Ai;

namespace Application.Abstractions.Diagnostics;

/// <summary>A numeric observation with provider-independent units; absent values are never zero-filled.</summary>
public sealed record DeploymentMetricSample(string Name, double Value, string Unit);

/// <summary>Immutable redacted delivery envelope shared by storage adapters.</summary>
public sealed record DeploymentLogEnvelope(
    Guid EventId,
    Guid TenantId,
    long OrderId,
    DateTimeOffset StoredAt,
    DateTimeOffset ExpiresAt,
    DeploymentDiagnosticEvent Event,
    string? Channel);

/// <summary>Writes redacted log batches to a specialized store.</summary>
public interface IDeploymentLogWriter
{
    /// <summary>Accepts an at-least-once batch; event identities remain unchanged on retry.</summary>
    Task WriteAsync(IReadOnlyList<DeploymentLogEnvelope> events, CancellationToken cancellationToken);
}

/// <summary>Reads bounded deployment output from a specialized store.</summary>
public interface IDeploymentLogQuery
{
    /// <summary>Backend filtering occurs before its result limit; unsupported older adapters fail explicitly.</summary>
    Task<IReadOnlyList<DeploymentLogEnvelope>> ReadAssessmentAsync(ArchiveAssessmentQuery query,
        CancellationToken token)
    {
        throw new NotSupportedException("Selected backend queries are unavailable.");
    }

    /// <summary>Reads one cursor page in forward or reverse order within the retention window.</summary>
    Task<IReadOnlyList<DeploymentLogEnvelope>> ReadAsync(Guid tenantId, Guid projectId, Guid deploymentId,
        long cursor, bool backwards, int limit, CancellationToken cancellationToken, DateTimeOffset? start = null);

    /// <summary>Confirms exact event identities are queryable before removing their durable buffer.</summary>
    Task<bool> ContainsAsync(IReadOnlyList<DeploymentLogEnvelope> events, CancellationToken cancellationToken);
}

/// <summary>Writes ordered numeric sample batches.</summary>
public interface IDeploymentMetricWriter
{
    /// <summary>Writes samples for one tenant with retries preserving timestamps and values.</summary>
    Task WriteAsync(IReadOnlyList<DeploymentLogEnvelope> events, CancellationToken cancellationToken);
}

/// <summary>A bounded aggregate point returned for historical charts.</summary>
public sealed record DeploymentMetricPoint(
    string Container,
    string Name,
    string Unit,
    DateTimeOffset Timestamp,
    double Average,
    double Minimum,
    double Maximum);

/// <summary>Bounded metric history with an explicit availability or omission notice.</summary>
public sealed record DeploymentMetricHistory(IReadOnlyList<DeploymentMetricPoint> Points, string? Availability = null);

/// <summary>Queries numeric deployment history and confirms delivery visibility.</summary>
public interface IDeploymentMetricQuery
{
    /// <summary>Selected containers are independent of chosen log sources.</summary>
    async Task<IReadOnlyList<DeploymentMetricPoint>> ReadAssessmentAsync(ArchiveAssessmentQuery query,
        CancellationToken token)
    {
        return (await ReadAsync(query.Tenant, query.Project, query.Deployment,
                query.Window.Start, query.Window.End, 100, token))
            .Where(p => query.Selection.MetricContainers is null ||
                        query.Selection.MetricContainers.Contains(p.Container, StringComparer.Ordinal)).ToArray();
    }

    /// <summary>Returns interval aggregates without synthesizing missing samples.</summary>
    Task<IReadOnlyList<DeploymentMetricPoint>> ReadAsync(Guid tenantId, Guid projectId, Guid deploymentId,
        DateTimeOffset start, DateTimeOffset end, int maximumPoints, CancellationToken cancellationToken);

    /// <summary>Confirms every exact sample is queryable.</summary>
    Task<bool> ContainsAsync(IReadOnlyList<DeploymentLogEnvelope> events, CancellationToken cancellationToken);
}

/// <summary>Authorized application boundary for history and runtime collection preferences.</summary>
public interface IDeploymentHistoryService
{
    async Task<TelemetryLogPage> ReadLogsV2Async(Guid user, Guid project, Guid deployment, string? cursor,
        bool backwards, int limit, string? search = null, CancellationToken token = default)
    {
        var history = await ReadLogsAsync(user, project, deployment,
            TelemetryHistoryCursor.Decode(cursor, project, deployment),
            backwards, limit, token);
        return TelemetryHistoryCursor.Page(history, project, deployment);
    }

    /// <summary>Reads owner-scoped automatic collection status and processing location.</summary>
    Task<DeploymentTelemetryPreferences> GetPreferencesAsync(Guid userId, Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>Compatibility operation; authorizes the owner and keeps approved telemetry storage enabled.</summary>
    Task SetManagedConsentAsync(Guid userId, Guid projectId, bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>Reads terminal output only after checking project and deployment ownership.</summary>
    Task<DeploymentTerminalHistory> ReadLogsAsync(Guid userId, Guid projectId, Guid deploymentId,
        long cursor, bool backwards, int limit, CancellationToken cancellationToken = default);

    /// <summary>Reads bounded numeric history only after checking ownership.</summary>
    Task<DeploymentMetricHistory> ReadMetricsAsync(Guid userId, Guid projectId, Guid deploymentId,
        DateTimeOffset start, DateTimeOffset end, int maximumPoints, CancellationToken cancellationToken = default);

    /// <summary>Compatibility operation; authorizes the owner and keeps runtime collection enabled.</summary>
    Task SetRuntimeCollectionAsync(Guid userId, Guid projectId, bool enabled,
        CancellationToken cancellationToken = default);
}

/// <summary>Non-secret owner-facing collection preferences.</summary>
public sealed record DeploymentTelemetryPreferences(
    bool RuntimeEnabled,
    bool ManagedConsent,
    bool ManagedService,
    string ProcessingRegion);