using Application.Abstractions.Diagnostics;

namespace Infrastructure.Diagnostics;

/// <summary>Snapshots and redacts store-bound batches before serialization, including direct adapter callers.</summary>
internal static class TelemetryWriteBoundary
{
    /// <summary>Rejects cross-tenant batches and returns detached safe event/channel copies without changing receipts.</summary>
    internal static DeploymentLogEnvelope[] Snapshot(IReadOnlyList<DeploymentLogEnvelope> events,
        IDiagnosticRedactor redactor,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var batch = events.Take(2_001).ToArray();
        if (batch.Length > 2_000) throw new ArgumentException("Telemetry write batch exceeds its supported bound.");
        if (batch.Length == 0) return [];
        if (batch.Any(item => !Enum.IsDefined(item.Event.Source) || !Enum.IsDefined(item.Event.Severity) ||
                              !Enum.IsDefined(item.Event.Kind) || !Enum.IsDefined(item.Event.TerminalChannel.Kind)))
            throw new ArgumentException("Telemetry write batch contains unsupported diagnostic categories.");
        var tenant = batch[0].TenantId;
        if (tenant == Guid.Empty || batch.Any(item => item.TenantId != tenant))
            throw new ArgumentException("Telemetry write batch requires a single tenant.");
        return batch.Select(item => item with
        {
            Event = redactor.Redact(item.Event).Event,
            Channel = item.Channel is null ? null : redactor.RedactText(item.Channel, 128)
        }).ToArray();
    }
}