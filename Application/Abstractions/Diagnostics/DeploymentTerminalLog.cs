namespace Application.Abstractions.Diagnostics;

/// <summary>A persisted, redacted terminal message with a database ordering cursor.</summary>
public sealed record DeploymentTerminalLog(
    long OrderId,
    Guid ProjectId,
    Guid? DeploymentId,
    string TerminalChannel,
    string Message);

/// <summary>A bounded page of terminal history.</summary>
public sealed record DeploymentTerminalHistory(IReadOnlyList<DeploymentTerminalLog> Events, bool EarlierOmitted);