namespace Application.Abstractions.Diagnostics;

/// <summary>A redacted terminal message; positive IDs are durable cursors and negative IDs identify unsaved live output.</summary>
public sealed record DeploymentTerminalLog(
    long OrderId,
    Guid ProjectId,
    Guid? DeploymentId,
    string TerminalChannel,
    string Message,
    Guid? EventId = null);

/// <summary>A bounded page of terminal history.</summary>
public sealed record DeploymentTerminalHistory(
    IReadOnlyList<DeploymentTerminalLog> Events,
    bool EarlierOmitted,
    string? Availability = null,
    bool CanAdvanceCursor = true);