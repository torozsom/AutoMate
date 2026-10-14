using System.Collections.Concurrent;
using Application.Abstractions.Diagnostics.Events;
using Application.Diagnostics;

namespace Infrastructure.Azure.ContainerApps.Runtime;

/// <summary>
///     Owns deployment/source outage deduplication and recovery counters for one runtime coordinator.
/// </summary>
/// <param name="diagnostics">Accepts the existing source-aware availability annotations.</param>
internal sealed class AzureRuntimeAvailability(IDeploymentDiagnosticPublisher diagnostics)
{
    /// <summary>Reported issue identities shared across overlapping polling passes.</summary>
    private readonly ConcurrentDictionary<string, string> _reportedIssues = new();

    /// <summary>
    ///     Publishes a deduplicated source-availability notice outside ordinary terminal output.
    /// </summary>
    /// <param name="target">Verified project, deployment and provider resource inventory.</param>
    /// <param name="issue">Finite source-availability issue identifier.</param>
    /// <param name="message">The message supplied to the operation.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    internal async Task ReportAsync(ContainerAppStreamTarget target, string issue, string message,
        CancellationToken cancellationToken)
    {
        var key = $"{target.DeploymentId:N}:{issue}";
        if (!_reportedIssues.TryAdd(key, message)) return;
        var database = issue.StartsWith("db-", StringComparison.Ordinal) ||
                       issue.StartsWith("database-", StringComparison.Ordinal);
        var informational = database && (issue.EndsWith("-empty", StringComparison.Ordinal) ||
                                         issue.EndsWith("-metrics-only", StringComparison.Ordinal));
        if (issue is "token" or "console" or "system" or "coordinator")
            AutoMateTelemetry.CollectorErrors.Add(1,
                new KeyValuePair<string, object?>("deployment.source", "AzureContainerApps"));
        await diagnostics.PublishAsync(new DeploymentDiagnosticEvent(target.ProjectId, target.DeploymentId,
            DeploymentDiagnosticSource.AzureContainerApps, DeploymentDiagnosticKind.Annotation,
            informational ? DeploymentDiagnosticSeverity.Information : DeploymentDiagnosticSeverity.Warning,
            DateTimeOffset.UtcNow, $"[Azure runtime] {message}",
            database
                ? new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, "azure-db")
                : new DeploymentTerminalChannel(DeploymentTerminalChannelKind.System),
            new Dictionary<string, string> { ["issue"] = issue },
            SourceIdentity: new DeploymentDiagnosticSourceIdentity(
                database ? DeploymentDiagnosticComponent.Database : DeploymentDiagnosticComponent.Revision,
                DeploymentDiagnosticStream.System, target.ContainerAppName)), cancellationToken);
    }

    /// <summary>
    ///     Clears a recovered source-availability issue so a later failure can be reported.
    /// </summary>
    /// <param name="target">Verified project, deployment and provider resource inventory.</param>
    /// <param name="issue">Finite source-availability issue identifier.</param>
    internal void Clear(ContainerAppStreamTarget target, string issue)
    {
        if (_reportedIssues.TryRemove($"{target.DeploymentId:N}:{issue}", out _) &&
            issue is "token" or "console" or "system")
            AutoMateTelemetry.CollectorReconnects.Add(1,
                new KeyValuePair<string, object?>("deployment.source", "AzureContainerApps"));
    }
}
