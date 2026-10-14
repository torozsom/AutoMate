using Application.Abstractions.Ai.Context;
using Application.Abstractions.Diagnostics.Events;
using Application.Abstractions.Diagnostics.History;

namespace Application.Abstractions.Diagnostics.Metrics;

/// <summary>
///     A numeric observation with provider-independent units; absent values are never zero-
///     filled.
/// </summary>
/// <param name="Name">The recorded display name or fixed metric identity.</param>
/// <param name="Value">The observed value in the recorded native unit.</param>
/// <param name="Unit">The native metric unit associated with the sample.</param>
public sealed record DeploymentMetricSample(string Name, double Value, string Unit);

/// <summary>
///     Shared finite metric series and native units accepted by transport and storage.
/// </summary>
public static class DeploymentMetricCatalog
{
    /// <summary>
    ///     Metric names are fixed; arbitrary series and units are never sent to a browser or
    ///     backend.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Units { get; } = new Dictionary<string, string>
    {
        ["automate_cpu_usage_cores"] = "cores",
        ["automate_memory_used_bytes"] = "bytes",
        ["automate_memory_limit_bytes"] = "bytes",
        ["automate_cpu_usage_percent"] = "percent",
        ["automate_memory_used_percent"] = "percent",
        ["automate_database_ru_usage_percent"] = "percent"
    };
}

/// <summary>
///     Redacted live numeric observations with the deployment identity needed to reject stale
///     deliveries.
/// </summary>
/// <param name="ProjectId">The project identity used for authorization or correlation.</param>
/// <param name="DeploymentId">The exact deployment identity used for ownership or correlation.</param>
/// <param name="ResourceName">The bounded provider resource name used to identify observations.</param>
/// <param name="Component">The typed deployment component that produced the observation.</param>
/// <param name="Timestamp">The provider observation timestamp, if known.</param>
/// <param name="Samples">Typed numeric resource samples with their native units.</param>
public sealed record DeploymentRuntimeMetricSnapshot(
    Guid ProjectId,
    Guid DeploymentId,
    string ResourceName,
    DeploymentDiagnosticComponent Component,
    DateTimeOffset Timestamp,
    IReadOnlyList<DeploymentMetricSample> Samples);

/// <summary>
///     Immutable redacted delivery envelope shared by storage adapters.
/// </summary>
/// <param name="EventId">The stable event identity used to deduplicate retries.</param>
/// <param name="TenantId">The internal owner-account telemetry identity.</param>
/// <param name="OrderId">
///     The durable ordering position; negative values represent unsaved live observations.
/// </param>
/// <param name="StoredAt">The UTC time at which the immutable envelope was durably stored.</param>
/// <param name="ExpiresAt">The expiry time of the token or stored envelope, if known.</param>
/// <param name="Event">The normalized diagnostic observation.</param>
/// <param name="Channel">The stable terminal channel used for routing.</param>
public sealed record DeploymentLogEnvelope(
    Guid EventId,
    Guid TenantId,
    long OrderId,
    DateTimeOffset StoredAt,
    DateTimeOffset ExpiresAt,
    DeploymentDiagnosticEvent Event,
    string? Channel);

/// <summary>
///     Writes redacted log batches to a specialized store.
/// </summary>
public interface IDeploymentLogWriter
{
    /// <summary>
    ///     Accepts an at-least-once batch; event identities remain unchanged on retry.
    /// </summary>
    /// <param name="events">The bounded batch of redacted diagnostic envelopes.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task that completes after the redacted batch is written.</returns>
    Task WriteAsync(IReadOnlyList<DeploymentLogEnvelope> events, CancellationToken cancellationToken);
}

/// <summary>
///     Reads bounded deployment output from a specialized store.
/// </summary>
public interface IDeploymentLogQuery
{
    /// <summary>
    ///     Backend filtering occurs before its result limit; unsupported older adapters fail
    ///     explicitly.
    /// </summary>
    /// <param name="query">The frozen resource scope, selection and bounded read options.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning the selected bounded redacted log envelopes.</returns>
    /// <exception cref="NotSupportedException">
    ///     The selected adapter does not implement filtered assessment reads.
    /// </exception>
    Task<IReadOnlyList<DeploymentLogEnvelope>> ReadAssessmentAsync(ArchiveAssessmentQuery query,
        CancellationToken token)
    {
        throw new NotSupportedException("Selected backend queries are unavailable.");
    }

    /// <summary>
    ///     Reads one cursor page in forward or reverse order within the retention window.
    /// </summary>
    /// <param name="tenantId">The internal owner-account telemetry identity.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="cursor">The previous read position; an absent cursor selects the initial page.</param>
    /// <param name="backwards">Whether to read earlier records rather than records after the cursor.</param>
    /// <param name="limit">The maximum number of returned or processed records.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <param name="start">The lower bound of the selected observation window.</param>
    /// <returns>A task returning a bounded ordered page of redacted log envelopes.</returns>
    Task<IReadOnlyList<DeploymentLogEnvelope>> ReadAsync(Guid tenantId, Guid projectId, Guid deploymentId,
        long cursor, bool backwards, int limit, CancellationToken cancellationToken, DateTimeOffset? start = null);

    /// <summary>
    ///     Confirms exact event identities are queryable before removing their durable buffer.
    /// </summary>
    /// <param name="events">The bounded batch of redacted diagnostic envelopes.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning whether every event identity is queryable in the specialized store.</returns>
    Task<bool> ContainsAsync(IReadOnlyList<DeploymentLogEnvelope> events, CancellationToken cancellationToken);
}

/// <summary>
///     Writes ordered numeric sample batches.
/// </summary>
public interface IDeploymentMetricWriter
{
    /// <summary>
    ///     Writes samples for one tenant with retries preserving timestamps and values.
    /// </summary>
    /// <param name="events">The bounded batch of redacted diagnostic envelopes.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task that completes after the redacted batch is written.</returns>
    Task WriteAsync(IReadOnlyList<DeploymentLogEnvelope> events, CancellationToken cancellationToken);
}

/// <summary>
///     A bounded aggregate point returned for historical charts.
/// </summary>
/// <param name="Container">
///     The recorded container identity; null selects all containers where supported.
/// </param>
/// <param name="Name">The recorded display name or fixed metric identity.</param>
/// <param name="Unit">The native metric unit associated with the sample.</param>
/// <param name="Timestamp">The provider observation timestamp, if known.</param>
/// <param name="Average">The observed arithmetic mean; absent values remain unknown.</param>
/// <param name="Minimum">The observed minimum in the recorded native unit.</param>
/// <param name="Maximum">The observed maximum in the recorded native unit.</param>
public sealed record DeploymentMetricPoint(
    string Container,
    string Name,
    string Unit,
    DateTimeOffset Timestamp,
    double Average,
    double Minimum,
    double Maximum);

/// <summary>
///     Bounded metric history with an explicit availability or omission notice.
/// </summary>
/// <param name="Points">The recorded metric aggregate points.</param>
/// <param name="Availability">
///     A safe availability or coverage notice; null indicates no reported notice.
/// </param>
public sealed record DeploymentMetricHistory(IReadOnlyList<DeploymentMetricPoint> Points, string? Availability = null);

/// <summary>
///     Queries numeric deployment history and confirms delivery visibility.
/// </summary>
public interface IDeploymentMetricQuery
{
    /// <summary>
    ///     Selected containers are independent of chosen log sources.
    /// </summary>
    /// <param name="query">The frozen resource scope, selection and bounded read options.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning the selected bounded numeric aggregate points.</returns>
    /// <remarks>
    ///     The compatibility implementation reads up to 100 points before applying the container
    ///     filter.
    /// </remarks>
    async Task<IReadOnlyList<DeploymentMetricPoint>> ReadAssessmentAsync(ArchiveAssessmentQuery query,
        CancellationToken token)
    {
        return (await ReadAsync(query.Tenant, query.Project, query.Deployment,
                query.Window.Start, query.Window.End, 100, token))
            .Where(p => query.Selection.MetricContainers is null ||
                        query.Selection.MetricContainers.Contains(p.Container, StringComparer.Ordinal)).ToArray();
    }

    /// <summary>
    ///     Returns interval aggregates without synthesizing missing samples.
    /// </summary>
    /// <param name="tenantId">The internal owner-account telemetry identity.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="start">The lower bound of the selected observation window.</param>
    /// <param name="end">The upper bound of the selected observation window.</param>
    /// <param name="maximumPoints">The maximum number of returned aggregate points.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning recorded bounded metric aggregates without zero-filling missing
    ///     observations.
    /// </returns>
    Task<IReadOnlyList<DeploymentMetricPoint>> ReadAsync(Guid tenantId, Guid projectId, Guid deploymentId,
        DateTimeOffset start, DateTimeOffset end, int maximumPoints, CancellationToken cancellationToken);

    /// <summary>
    ///     Confirms every exact sample is queryable.
    /// </summary>
    /// <param name="events">The bounded batch of redacted diagnostic envelopes.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning whether every exact numeric sample is queryable in the specialized
    ///     store.
    /// </returns>
    Task<bool> ContainsAsync(IReadOnlyList<DeploymentLogEnvelope> events, CancellationToken cancellationToken);
}

/// <summary>
///     Provides owner-authorized history reads and compatibility telemetry preference
///     operations.
/// </summary>
public interface IDeploymentHistoryService
{
    /// <summary>
    ///     Reads authorized terminal history using versioned resource-scoped navigation cursors.
    /// </summary>
    /// <param name="user">The internal user identity used for authorization.</param>
    /// <param name="project">The project identity used to scope the operation.</param>
    /// <param name="deployment">The exact deployment identity used to scope the operation.</param>
    /// <param name="cursor">The previous read position; an absent cursor selects the initial page.</param>
    /// <param name="backwards">Whether to read earlier records rather than records after the cursor.</param>
    /// <param name="limit">The maximum number of returned or processed records.</param>
    /// <param name="search">The optional plain-text search term.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning an owned terminal page with opaque navigation cursors and availability
    ///     metadata.
    /// </returns>
    /// <exception cref="ArgumentException">
    ///     The cursor is malformed, oversized, nonpositive, or belongs to a different resource.
    /// </exception>
    /// <remarks>
    ///     The compatibility implementation adapts legacy cursor reads and does not apply the
    ///     search term.
    /// </remarks>
    async Task<TelemetryLogPage> ReadLogsV2Async(Guid user, Guid project, Guid deployment, string? cursor,
        bool backwards, int limit, string? search = null, CancellationToken token = default)
    {
        var history = await ReadLogsAsync(user, project, deployment,
            TelemetryHistoryCursor.Decode(cursor, project, deployment),
            backwards, limit, token);
        return TelemetryHistoryCursor.Page(history, project, deployment);
    }

    /// <summary>
    ///     Reads owner-scoped automatic collection status and processing location.
    /// </summary>
    /// <param name="userId">The internal AutoMate user identity.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning the owned saved selection and source catalog, or null for missing or
    ///     unauthorized deployments.
    /// </returns>
    Task<DeploymentTelemetryPreferences> GetPreferencesAsync(Guid userId, Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Compatibility operation; authorizes the owner and keeps approved telemetry storage
    ///     enabled.
    /// </summary>
    /// <param name="userId">The internal AutoMate user identity.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="enabled">
    ///     The legacy preference value; approved automatic collection remains enabled.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task that completes after owner authorization and the compatibility storage update.</returns>
    Task SetManagedConsentAsync(Guid userId, Guid projectId, bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads terminal output only after checking project and deployment ownership.
    /// </summary>
    /// <param name="userId">The internal AutoMate user identity.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="cursor">The previous read position; an absent cursor selects the initial page.</param>
    /// <param name="backwards">Whether to read earlier records rather than records after the cursor.</param>
    /// <param name="limit">The maximum number of returned or processed records.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning owned terminal output with explicit omissions and storage availability.
    /// </returns>
    Task<DeploymentTerminalHistory> ReadLogsAsync(Guid userId, Guid projectId, Guid deploymentId,
        long cursor, bool backwards, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads bounded numeric history only after checking ownership.
    /// </summary>
    /// <param name="userId">The internal AutoMate user identity.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="start">The lower bound of the selected observation window.</param>
    /// <param name="end">The upper bound of the selected observation window.</param>
    /// <param name="maximumPoints">The maximum number of returned aggregate points.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning bounded numeric aggregates and any storage-availability notice.</returns>
    Task<DeploymentMetricHistory> ReadMetricsAsync(Guid userId, Guid projectId, Guid deploymentId,
        DateTimeOffset start, DateTimeOffset end, int maximumPoints, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Compatibility operation; authorizes the owner and keeps runtime collection enabled.
    /// </summary>
    /// <param name="userId">The internal AutoMate user identity.</param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="enabled">
    ///     The legacy preference value; approved automatic collection remains enabled.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task that completes after owner authorization and the compatibility collection update.
    /// </returns>
    Task SetRuntimeCollectionAsync(Guid userId, Guid projectId, bool enabled,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Non-secret owner-facing collection preferences.
/// </summary>
/// <param name="RuntimeEnabled">The retained compatibility flag for automatic runtime collection.</param>
/// <param name="ManagedConsent">
///     The retained compatibility flag for approved automatic telemetry storage.
/// </param>
/// <param name="ManagedService">Whether telemetry processing uses the managed service.</param>
/// <param name="ProcessingRegion">The configured diagnostic processing region.</param>
public sealed record DeploymentTelemetryPreferences(
    bool RuntimeEnabled,
    bool ManagedConsent,
    bool ManagedService,
    string ProcessingRegion);
