namespace Application.Abstractions.Diagnostics;

/// <summary>A redacted terminal message; positive IDs are durable cursors and negative IDs identify unsaved live output.</summary>
/// <param name="TraceId">Optional canonical trace identity retained for bounded evidence correlation.</param>
/// <param name="SpanId">Optional canonical span identity; not a fetched trace record.</param>
/// <param name="Sequence">Actual provider sequence when available; never synthesized from a cursor.</param>
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
    long? Sequence = null)
{
    /// <summary>Preserves safe source metadata identically for live delivery and specialized-store replay.</summary>
    public static DeploymentTerminalLog FromEvent(long orderId, DeploymentDiagnosticEvent e, string channel)
    {
        return new DeploymentTerminalLog(orderId, e.ProjectId, e.DeploymentId, channel, e.Message, e.EventId,
            e.SourceIdentity?.Stream, e.Severity, e.TimestampUtc, e.SourceIdentity?.InstanceId, e.Cursor,
            e.TraceId, e.SpanId, e.Sequence);
    }
}

/// <summary>A bounded page of terminal history.</summary>
public sealed record DeploymentTerminalHistory(
    IReadOnlyList<DeploymentTerminalLog> Events,
    bool EarlierOmitted,
    string? Availability = null,
    bool CanAdvanceCursor = true);