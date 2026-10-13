namespace Application.Abstractions.Diagnostics;

/// <summary>
///     A redacted terminal message; positive IDs are durable cursors and negative IDs identify
///     unsaved live output.
/// </summary>
/// <param name="OrderId">
///     The durable ordering position; negative values represent unsaved live observations.
/// </param>
/// <param name="ProjectId">The project identity used for authorization or correlation.</param>
/// <param name="DeploymentId">
///     The deployment identity; null is used only where legacy observations permit it.
/// </param>
/// <param name="TerminalChannel">The stable terminal routing channel.</param>
/// <param name="Message">The message supplied to the operation.</param>
/// <param name="EventId">The stable event identity used to deduplicate retries.</param>
/// <param name="Stream">The typed diagnostic output stream.</param>
/// <param name="Severity">The normalized severity used to rank a context group.</param>
/// <param name="TimestampUtc">The original UTC observation timestamp, if recorded.</param>
/// <param name="SourceInstanceId">The bounded non-secret provider instance identity, if known.</param>
/// <param name="SourceCursor">The provider-local cursor, if recorded.</param>
/// <param name="TraceId">
///     Optional canonical trace identity retained for bounded evidence correlation.
/// </param>
/// <param name="SpanId">Optional canonical span identity; not a fetched trace record.</param>
/// <param name="Sequence">Actual provider sequence when available; never synthesized from a cursor.</param>
/// <param name="IsAvailabilityNotice">
///     Whether the record contains operational guidance rather than terminal output.
/// </param>
public sealed record DeploymentTerminalLog(
    long OrderId,
    Guid ProjectId,
    Guid? DeploymentId,
    string TerminalChannel,
    string Message,
    Guid? EventId = null,
    DeploymentDiagnosticStream? Stream = null,
    DeploymentDiagnosticSeverity? Severity = null,
    DateTimeOffset? TimestampUtc = null,
    string? SourceInstanceId = null,
    string? SourceCursor = null,
    string? TraceId = null,
    string? SpanId = null,
    long? Sequence = null,
    bool IsAvailabilityNotice = false)
{
    /// <summary>
    ///     Preserves safe source metadata identically for live delivery and specialized-store
    ///     replay.
    /// </summary>
    /// <param name="orderId">
    ///     The durable ordering position; negative values represent unsaved live observations.
    /// </param>
    /// <param name="e">The normalized deployment observation.</param>
    /// <param name="channel">The stable terminal channel used for routing.</param>
    /// <returns>A terminal projection preserving the event's safe source and replay metadata.</returns>
    public static DeploymentTerminalLog FromEvent(long orderId, DeploymentDiagnosticEvent e, string channel)
    {
        return new DeploymentTerminalLog(orderId, e.ProjectId, e.DeploymentId, channel, e.Message, e.EventId,
            e.SourceIdentity?.Stream, e.Severity, e.TimestampUtc, e.SourceIdentity?.InstanceId, e.Cursor,
            e.TraceId, e.SpanId, e.Sequence,
            e.Source == DeploymentDiagnosticSource.AzureContainerApps &&
            e.Kind == DeploymentDiagnosticKind.Annotation && e.Attributes?.ContainsKey("issue") == true);
    }
}

/// <summary>
///     A bounded page of terminal history.
/// </summary>
/// <param name="Events">The bounded batch of redacted diagnostic envelopes.</param>
/// <param name="EarlierOmitted">
///     Whether earlier records were excluded by retention or bounded selection.
/// </param>
/// <param name="Availability">
///     A safe availability or coverage notice; null indicates no reported notice.
/// </param>
/// <param name="CanAdvanceCursor">
///     Whether storage confirmation permits the caller to advance its replay cursor.
/// </param>
public sealed record DeploymentTerminalHistory(
    IReadOnlyList<DeploymentTerminalLog> Events,
    bool EarlierOmitted,
    string? Availability = null,
    bool CanAdvanceCursor = true);
