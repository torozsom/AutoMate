using Application.Abstractions.Ai;

namespace Application.Abstractions.Diagnostics;

/// <summary>
///     Private gateway request for bounded archived metric aggregation.
/// </summary>
/// <param name="Tenant">The internal owner-account telemetry scope.</param>
/// <param name="Project">The project identity used to scope the operation.</param>
/// <param name="Deployment">The exact deployment identity used to scope the operation.</param>
/// <param name="Start">The lower bound of the selected observation window.</param>
/// <param name="End">The upper bound of the selected observation window.</param>
/// <param name="MaximumPoints">The maximum number of returned aggregate points.</param>
public sealed record ArchiveMetricRequest(
    Guid Tenant,
    Guid Project,
    Guid Deployment,
    DateTimeOffset Start,
    DateTimeOffset End,
    int MaximumPoints);

/// <summary>
///     Private cleanup request allowed only for a committed deletion-outbox item.
/// </summary>
/// <param name="Tenant">The internal owner-account telemetry scope.</param>
/// <param name="Project">The project identity used to scope the operation.</param>
public sealed record ArchiveDeleteRequest(Guid Tenant, Guid Project);

/// <summary>
///     Retained backend interval statistics; these are not raw sample counts.
/// </summary>
/// <param name="Tenant">The internal owner-account telemetry scope.</param>
/// <param name="Project">The project identity used to scope the operation.</param>
/// <param name="Deployment">The exact deployment identity used to scope the operation.</param>
/// <param name="Points">The recorded metric aggregate points.</param>
public sealed record ArchiveMetricImport(
    Guid Tenant,
    Guid Project,
    Guid Deployment,
    IReadOnlyList<DeploymentMetricPoint> Points);

/// <summary>
///     Permanent redacted deployment history, separate from the operational telemetry retention
///     window.
/// </summary>
public interface IDeploymentArchive
{
    /// <summary>
    ///     Filters before pagination and returns recorded source metadata without diagnostic prose
    ///     in catalogs.
    /// </summary>
    /// <param name="query">The frozen resource scope, selection and bounded read options.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning selected bounded evidence and recorded source metadata.</returns>
    /// <exception cref="NotSupportedException">
    ///     The selected adapter does not implement filtered assessment reads.
    /// </exception>
    Task<ArchiveAssessmentPage> ReadAssessmentAsync(
        ArchiveAssessmentQuery query, CancellationToken token)
    {
        throw new NotSupportedException("Archive assessment selection is unavailable.");
    }

    /// <summary>
    ///     Filters container identities before bounded metric aggregation.
    /// </summary>
    /// <param name="query">The frozen resource scope, selection and bounded read options.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning bounded metric aggregates for the selected containers and time window.
    /// </returns>
    /// <remarks>
    ///     The compatibility implementation reads up to 100 points before applying the container
    ///     filter.
    /// </remarks>
    async Task<IReadOnlyList<DeploymentMetricPoint>> ReadAssessmentMetricsAsync(
        ArchiveAssessmentQuery query, CancellationToken token)
    {
        return (await ReadMetricsAsync(query.Tenant, query.Project, query.Deployment, query.Window.Start,
                query.Window.End, 100, token))
            .Where(p => query.Selection.MetricContainers is null ||
                        query.Selection.MetricContainers.Contains(p.Container, StringComparer.Ordinal)).ToArray();
    }

    /// <summary>
    ///     Reads a bounded numeric partition through the rebuildable lookup.
    /// </summary>
    /// <param name="tenant">The internal owner-account telemetry scope.</param>
    /// <param name="project">The project identity used to scope the operation.</param>
    /// <param name="deployment">The exact deployment identity used to scope the operation.</param>
    /// <param name="range">The selected frozen observation window or assessment range.</param>
    /// <param name="container">
    ///     The recorded container identity; null selects all containers where supported.
    /// </param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning a bounded partition, or an empty batch with an availability notice when
    ///     unsupported.
    /// </returns>
    Task<ArchiveMetricBatch> ReadIndexedMetricsAsync(Guid tenant, Guid project, Guid deployment,
        MetricTimeRange range, string? container, CancellationToken token)
    {
        return Task.FromResult(new ArchiveMetricBatch([], null, "Detailed metric lookup is unavailable."));
    }

    /// <summary>
    ///     Returns a private owner-authorized page of numeric deployment partitions.
    /// </summary>
    /// <param name="request">The owned deployment partitions, frozen range and continuation to read.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning an authorized numeric batch, or an empty batch with an availability
    ///     notice when unsupported.
    /// </returns>
    Task<ArchiveMetricBatch> ReadMetricBatchAsync(ArchiveMetricBatchRequest request, CancellationToken token)
    {
        return Task.FromResult(new ArchiveMetricBatch([], null,
            "Detailed metric exploration is unavailable on this archive host."));
    }

    /// <summary>
    ///     Durably appends an immutable event; duplicate identities return the original receipt.
    /// </summary>
    /// <param name="envelope">The immutable redacted event and durable routing metadata.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning the durable original receipt, including when the event identity already
    ///     exists.
    /// </returns>
    Task<DeploymentLogEnvelope> AppendAsync(DeploymentLogEnvelope envelope, CancellationToken token);

    /// <summary>
    ///     Returns a bounded ordered log page with optional plain-text search.
    /// </summary>
    /// <param name="tenant">The internal owner-account telemetry scope.</param>
    /// <param name="project">The project identity used to scope the operation.</param>
    /// <param name="deployment">The exact deployment identity used to scope the operation.</param>
    /// <param name="cursor">The previous read position; an absent cursor selects the initial page.</param>
    /// <param name="backwards">Whether to read earlier records rather than records after the cursor.</param>
    /// <param name="limit">The maximum number of returned or processed records.</param>
    /// <param name="search">The optional plain-text search term.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning a bounded ordered page of redacted log envelopes.</returns>
    Task<IReadOnlyList<DeploymentLogEnvelope>> ReadAsync(Guid tenant, Guid project, Guid deployment,
        long cursor, bool backwards, int limit, string? search, CancellationToken token);

    /// <summary>
    ///     Aggregates retained numeric samples without zero-filling missing intervals.
    /// </summary>
    /// <param name="tenant">The internal owner-account telemetry scope.</param>
    /// <param name="project">The project identity used to scope the operation.</param>
    /// <param name="deployment">The exact deployment identity used to scope the operation.</param>
    /// <param name="start">The lower bound of the selected observation window.</param>
    /// <param name="end">The upper bound of the selected observation window.</param>
    /// <param name="maximumPoints">The maximum number of returned aggregate points.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning recorded bounded numeric aggregates without zero-filling missing
    ///     intervals.
    /// </returns>
    Task<IReadOnlyList<DeploymentMetricPoint>> ReadMetricsAsync(Guid tenant, Guid project, Guid deployment,
        DateTimeOffset start, DateTimeOffset end, int maximumPoints, CancellationToken token);

    /// <summary>
    ///     Removes one deleted project's partition; repeated cleanup is harmless.
    /// </summary>
    /// <param name="tenant">The internal owner-account telemetry scope.</param>
    /// <param name="project">The project identity used to scope the operation.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task that completes after the deleted project's archived partition is removed.</returns>
    Task DeleteProjectAsync(Guid tenant, Guid project, CancellationToken token);

    /// <summary>
    ///     Idempotently imports still-available interval statistics from the operational backend.
    /// </summary>
    /// <param name="import">The retained interval statistics to import idempotently.</param>
    /// <param name="token">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task that completes after retained interval statistics are imported.</returns>
    Task ImportMetricsAsync(ArchiveMetricImport import, CancellationToken token);
}