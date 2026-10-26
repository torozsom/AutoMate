using System.Text;
using Application.Abstractions.Diagnostics.Events;
using Application.Abstractions.Diagnostics.History;
using Application.Abstractions.Diagnostics.Redaction;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Diagnostics.Storage;

/// <summary>
///     Reads and expires legacy PostgreSQL diagnostics; new payload writes are prohibited.
/// </summary>
/// <param name="dbContext">
///     Application metadata context used for ownership, leases and persisted state.
/// </param>
/// <param name="redactor">Applies shared credential and unsafe-text masking.</param>
public sealed class DeploymentDiagnosticStore(AutoMateDbContext dbContext, IDiagnosticRedactor redactor)
    : IDeploymentDiagnosticStore
{
    /// <summary>
    ///     Retains the legacy persistence contract; current PostgreSQL adapters reject new
    ///     payloads.
    /// </summary>
    /// <param name="diagnosticEvent">Normalized diagnostic to redact before persistence or presentation.</param>
    /// <param name="terminalChannel">The stable terminal routing channel.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>The persisted ordering identifier if an adapter supports legacy writes.</returns>
    /// <exception cref="InvalidOperationException">
    ///     New diagnostic payloads require the Loki/Mimir disk gateway.
    /// </exception>
    /// <exception cref="OperationCanceledException">The supplied cancellation token is canceled.</exception>
    public Task<long> PersistAsync(DeploymentDiagnosticEvent diagnosticEvent, string? terminalChannel,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("New diagnostic payloads require the Loki/Mimir disk gateway.");
    }

    /// <summary>
    ///     Reads a bounded recent terminal-history page for one deployment.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="limit">The maximum number of returned or processed records.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning recent terminal records with explicit omissions and availability
    ///     metadata.
    /// </returns>
    public async Task<DeploymentTerminalHistory> ReadRecentAsync(Guid projectId, Guid deploymentId, int limit,
        CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 2_000);
        var now = DateTimeOffset.UtcNow;

        // Older cloud preparation and local build messages were stored without a deployment ID.
        // Confine those records to this deployment's lifetime within the authorized project.
        var deploymentStart = await dbContext.Deployments
            .AsNoTracking()
            .Where(item => item.Id == deploymentId
                           && item.CsProject!.AppId == projectId)
            .Select(item => (DateTimeOffset?)item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (deploymentStart is null) return new DeploymentTerminalHistory([], false);

        var nextDeploymentStart = await dbContext.Deployments
            .AsNoTracking()
            .Where(item => item.CsProject!.AppId == projectId
                           && item.CreatedAt > deploymentStart.Value)
            .OrderBy(item => item.CreatedAt)
            .Select(item => (DateTimeOffset?)item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var rows = await dbContext.DeploymentDiagnosticRecords
            .AsNoTracking()
            .Where(item =>
                item.ProjectId == projectId
                && item.ExpiresAt > now
                && ((item.DeploymentId == deploymentId && item.TerminalChannel != null)
                    || (item.DeploymentId == null
                        && (item.Source == "GitHubActions" || item.Source == "DockerCompose")
                        && item.TimestampUtc >= deploymentStart.Value
                        && (nextDeploymentStart == null || item.TimestampUtc < nextDeploymentStart.Value))))
            .OrderByDescending(item => item.OrderId)
            .Take(boundedLimit + 1)
            .Select(item => new DeploymentTerminalLog(item.OrderId, item.ProjectId, item.DeploymentId,
                item.TerminalChannel ?? (item.Source == "GitHubActions" ? "github-actions" : "build"), item.Message,
                item.Id, null, item.Severity == "Critical" ? DeploymentDiagnosticSeverity.Critical :
                item.Severity == "Error" ? DeploymentDiagnosticSeverity.Error :
                item.Severity == "Warning" ? DeploymentDiagnosticSeverity.Warning :
                item.Severity == "Information" ? DeploymentDiagnosticSeverity.Information :
                item.Severity == "Debug" ? DeploymentDiagnosticSeverity.Debug :
                item.Severity == "Trace" ? DeploymentDiagnosticSeverity.Trace : null,
                item.TimestampUtc, null, null, item.TraceId, item.SpanId, item.Sequence))
            .ToListAsync(cancellationToken);

        var earlierOmitted = rows.Count > boundedLimit;
        if (earlierOmitted) rows.RemoveAt(rows.Count - 1);
        rows.Reverse();
        return new DeploymentTerminalHistory([.. rows.Select(redactor.RedactTerminal)], earlierOmitted);
    }

    /// <summary>
    ///     Reads a bounded ordered page after a previously delivered terminal event.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="afterOrderId">The last confirmed ordering position; return only later records.</param>
    /// <param name="limit">The maximum number of returned or processed records.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>
    ///     A task returning ordered terminal records following the confirmed cursor, with coverage
    ///     metadata.
    /// </returns>
    public async Task<DeploymentTerminalHistory> ReadAfterAsync(Guid projectId, Guid deploymentId,
        long afterOrderId, int limit, CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 2_000);
        var now = DateTimeOffset.UtcNow;
        var rows = await dbContext.DeploymentDiagnosticRecords.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.DeploymentId == deploymentId &&
                           item.TerminalChannel != null && item.ExpiresAt > now &&
                           item.OrderId > afterOrderId)
            .OrderBy(item => item.OrderId).Take(boundedLimit + 1)
            .Select(item => new DeploymentTerminalLog(item.OrderId, item.ProjectId, item.DeploymentId,
                item.TerminalChannel!, item.Message, item.Id, null, item.Severity == "Critical"
                    ? DeploymentDiagnosticSeverity.Critical
                    : item.Severity == "Error"
                        ? DeploymentDiagnosticSeverity.Error
                        : item.Severity == "Warning"
                            ? DeploymentDiagnosticSeverity.Warning
                            : item.Severity == "Information"
                                ? DeploymentDiagnosticSeverity.Information
                                : item.Severity == "Debug"
                                    ? DeploymentDiagnosticSeverity.Debug
                                    : item.Severity == "Trace"
                                        ? DeploymentDiagnosticSeverity.Trace
                                        : null,
                item.TimestampUtc, null, null, item.TraceId, item.SpanId, item.Sequence))
            .ToListAsync(cancellationToken);
        var moreAvailable = rows.Count > boundedLimit;
        if (moreAvailable) rows.RemoveAt(rows.Count - 1);
        return new DeploymentTerminalHistory([.. rows.Select(redactor.RedactTerminal)], moreAvailable);
    }

    /// <summary>
    ///     Deletes a bounded batch of expired diagnostic records.
    /// </summary>
    /// <param name="limit">The maximum number of returned or processed records.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning the number of expired records deleted in this batch.</returns>
    public async Task<int> DeleteExpiredAsync(int limit, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var ids = await dbContext.DeploymentDiagnosticRecords.AsNoTracking()
            .Where(item => item.ExpiresAt <= now)
            .OrderBy(item => item.ExpiresAt)
            .Take(Math.Clamp(limit, 1, 10_000))
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        if (ids.Count == 0) return 0;
        return await dbContext.DeploymentDiagnosticRecords.Where(item => ids.Contains(item.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    ///     Builds bounded redacted compatibility context for one deployment.
    /// </summary>
    /// <param name="deploymentId">The exact deployment identity used for ownership or correlation.</param>
    /// <param name="maximumCharacters">The maximum permitted text length.</param>
    /// <param name="cancellationToken">Cancels the operation when supported by the underlying work.</param>
    /// <returns>A task returning bounded redacted diagnostic text suitable for legacy context callers.</returns>
    public async Task<string> BuildContextAsync(Guid deploymentId, int maximumCharacters,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var records = await dbContext.DeploymentDiagnosticRecords.AsNoTracking()
            .Where(item => item.DeploymentId == deploymentId && item.ExpiresAt > now)
            .OrderByDescending(item => item.Severity == "Critical").ThenByDescending(item => item.Severity == "Error")
            .ThenByDescending(item => item.TimestampUtc).Take(300).ToListAsync(cancellationToken);
        var context = new StringBuilder();
        foreach (var item in records.OrderBy(item => item.TimestampUtc))
        {
            var line = $"[{item.TimestampUtc:O}] [{item.Severity}] [{item.Source}/{item.Kind}] {item.Message}\n";
            if (context.Length + line.Length > maximumCharacters) break;
            context.Append(line);
        }

        return redactor.RedactText(context.ToString(), maximumCharacters);
    }
}
