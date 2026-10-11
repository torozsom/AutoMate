namespace Application.Abstractions.Diagnostics;

/// <summary>Durable ingestion and pending reads, independent of the queue technology.</summary>
public interface ITelemetryGateway
{
    /// <summary>Returns a stable receipt only after the queue has persisted the redacted event.</summary>
    Task<DeploymentLogEnvelope> AcceptAsync(DeploymentDiagnosticEvent diagnosticEvent, string? channel,
        CancellationToken cancellationToken);

    /// <summary>Returns a bounded resource-scoped view with explicit loss and truncation indicators.</summary>
    Task<TelemetryPendingHistory> ReadPendingAsync(Guid tenant, Guid project, Guid deployment,
        CancellationToken cancellationToken);
}

/// <summary>Internal wire request; ownership is derived by the ingestion service.</summary>
public sealed record TelemetryIngestRequest(DeploymentDiagnosticEvent Event, string? Channel);

/// <summary>Pending events remain readable during backend outages; loss is reported conservatively per owner.</summary>
public sealed record TelemetryPendingHistory(
    IReadOnlyList<DeploymentLogEnvelope> Events,
    long DroppedEvents,
    bool Truncated = false);

/// <summary>Collectors advance provider positions only after durable acceptance.</summary>
public interface IDurableDeploymentDiagnosticPublisher
{
    /// <summary>False means no durable receipt exists and the collector must retain its checkpoint.</summary>
    Task<bool> PublishDurablyAsync(DeploymentDiagnosticEvent diagnosticEvent, CancellationToken cancellationToken);
}