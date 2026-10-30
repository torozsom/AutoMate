using System.Text.Json.Serialization;

namespace Infrastructure.Diagnostics.Storage;

/// <summary>
///     Typed spool status preserving the existing private status response fields.
/// </summary>
/// <param name="PendingEvents">Durably buffered events.</param>
/// <param name="BufferedBytes">Bytes charged against the buffer budget.</param>
/// <param name="Segments">Pending segment count.</param>
/// <param name="OldestPendingSeconds">Age of the oldest pending receipt.</param>
/// <param name="DroppedEvents">Conservative persisted loss total.</param>
/// <param name="Accepting">Whether the ingestion writer is accepting work.</param>
/// <param name="ObservedAt">Snapshot timestamp, omitted from the compatibility response.</param>
public sealed record TelemetrySpoolSnapshot(
    int PendingEvents,
    long BufferedBytes,
    int Segments,
    double OldestPendingSeconds,
    long DroppedEvents,
    bool Accepting,
    [property: JsonIgnore] DateTimeOffset ObservedAt);
