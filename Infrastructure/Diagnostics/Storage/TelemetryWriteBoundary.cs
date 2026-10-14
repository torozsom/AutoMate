using Application.Abstractions.Diagnostics.Metrics;
using Application.Abstractions.Diagnostics.Redaction;

namespace Infrastructure.Diagnostics.Storage;

/// <summary>
///     Snapshots and redacts store-bound batches before serialization, including direct adapter
///     callers.
/// </summary>
internal static class TelemetryWriteBoundary
{
    /// <summary>
    ///     Rejects cross-tenant batches and returns detached safe event/channel copies without
    ///     changing receipts.
    /// </summary>
    /// <param name="events">The bounded batch of redacted diagnostic envelopes.</param>
    /// <param name="redactor">Applies shared credential and unsafe-text masking.</param>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>The snapshot selection with its documented bounds and ordering.</returns>
    /// <exception cref="ArgumentException">Telemetry write batch exceeds its supported bound.</exception>
    /// <exception cref="OperationCanceledException">The supplied cancellation token is canceled.</exception>
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
