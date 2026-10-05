using System.Text;
using Application.Abstractions.Diagnostics;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Diagnostics;

/// <summary>Reads and expires legacy PostgreSQL diagnostics; new payload writes are prohibited.</summary>
public sealed class DeploymentDiagnosticStore(AutoMateDbContext dbContext, IDiagnosticRedactor redactor)
    : IDeploymentDiagnosticStore
{
    /// <inheritdoc />
    public Task<long> PersistAsync(DeploymentDiagnosticEvent diagnosticEvent, string? terminalChannel,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("New diagnostic payloads require the Loki/Mimir disk gateway.");
    }

    /// <inheritdoc />
    public async Task<DeploymentTerminalHistory> ReadRecentAsync(Guid projectId, Guid deploymentId, int limit,
        CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 2_000);
        var now = DateTimeOffset.UtcNow;
        // Older cloud preparation and local build messages were stored without a deployment ID.
        // Confine those records to this deployment's lifetime within the authorized project.
        var deploymentStart = await dbContext.Deployments.AsNoTracking()
            .Where(item => item.Id == deploymentId && item.CsProject!.AppId == projectId)
            .Select(item => (DateTimeOffset?)item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (deploymentStart is null) return new DeploymentTerminalHistory([], false);
        var nextDeploymentStart = await dbContext.Deployments.AsNoTracking()
            .Where(item => item.CsProject!.AppId == projectId && item.CreatedAt > deploymentStart.Value)
            .OrderBy(item => item.CreatedAt)
            .Select(item => (DateTimeOffset?)item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var rows = await dbContext.DeploymentDiagnosticRecords.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.ExpiresAt > now &&
                           ((item.DeploymentId == deploymentId && item.TerminalChannel != null) ||
                            (item.DeploymentId == null &&
                             (item.Source == "GitHubActions" || item.Source == "DockerCompose") &&
                             item.TimestampUtc >= deploymentStart.Value &&
                             (nextDeploymentStart == null || item.TimestampUtc < nextDeploymentStart.Value))))
            .OrderByDescending(item => item.OrderId)
            .Take(boundedLimit + 1)
            .Select(item => new DeploymentTerminalLog(item.OrderId, item.ProjectId, item.DeploymentId,
                item.TerminalChannel ?? (item.Source == "GitHubActions" ? "github-actions" : "build"), item.Message,
                null, null, item.Severity == "Critical" ? DeploymentDiagnosticSeverity.Critical :
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
        return new DeploymentTerminalHistory(rows.Select(redactor.RedactTerminal).ToArray(), earlierOmitted);
    }

    /// <inheritdoc />
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
                item.TerminalChannel!, item.Message, null, null, item.Severity == "Critical"
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
        return new DeploymentTerminalHistory(rows.Select(redactor.RedactTerminal).ToArray(), moreAvailable);
    }

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

    /// <inheritdoc />
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