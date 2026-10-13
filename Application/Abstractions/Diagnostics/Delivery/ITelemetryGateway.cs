namespace Application.Abstractions.Diagnostics;

/// <summary>
///     Durable ingestion and pending reads, independent of the queue technology.
/// </summary>
public interface ITelemetryGateway
{
    /// <summary>
    ///     Returns a stable receipt only after the queue has persisted the redacted event.
    /// </summary>
    /// <param name="diagnosticEvent">The normalized diagnostic observation to process.</param>
    /// <param name="channel">The stable terminal channel used for routing.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning the stable receipt acknowledging durable storage of the redacted event.
    /// </returns>
    Task<DeploymentLogEnvelope> AcceptAsync(DeploymentDiagnosticEvent diagnosticEvent, string? channel,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Returns a bounded resource-scoped view with explicit loss and truncation indicators.
    /// </summary>
    /// <param name="tenant">The internal owner-account telemetry scope.</param>
    /// <param name="project">The project identity used to scope the operation.</param>
    /// <param name="deployment">The exact deployment identity used to scope the operation.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning a bounded pending-event view with explicit loss and truncation
    ///     metadata.
    /// </returns>
    Task<TelemetryPendingHistory> ReadPendingAsync(Guid tenant, Guid project, Guid deployment,
        CancellationToken cancellationToken);
}

/// <summary>
///     Internal wire request; ownership is derived by the ingestion service.
/// </summary>
/// <param name="Event">The normalized diagnostic observation.</param>
/// <param name="Channel">The stable terminal channel used for routing.</param>
public sealed record TelemetryIngestRequest(DeploymentDiagnosticEvent Event, string? Channel);

/// <summary>
///     Pending events remain readable during backend outages; loss is reported conservatively
///     per owner.
/// </summary>
/// <param name="Events">The bounded batch of redacted diagnostic envelopes.</param>
/// <param name="DroppedEvents">
///     The conservative count of events lost under the owner storage limits.
/// </param>
/// <param name="Truncated">Whether bounded selection omitted part of the requested evidence.</param>
public sealed record TelemetryPendingHistory(
    IReadOnlyList<DeploymentLogEnvelope> Events,
    long DroppedEvents,
    bool Truncated = false);

/// <summary>
///     Collectors advance provider positions only after durable acceptance.
/// </summary>
public interface IDurableDeploymentDiagnosticPublisher
{
    /// <summary>
    ///     False means no durable receipt exists and the collector must retain its checkpoint.
    /// </summary>
    /// <param name="diagnosticEvent">The normalized diagnostic observation to process.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning true only after durable acceptance; false requires retaining the
    ///     provider checkpoint.
    /// </returns>
    Task<bool> PublishDurablyAsync(DeploymentDiagnosticEvent diagnosticEvent, CancellationToken cancellationToken);
}
