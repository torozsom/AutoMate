using System.Security.Cryptography;
using System.Text;
using Application.Abstractions.Diagnostics;
using Application.Diagnostics;
using Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.Azure;

/// <summary>
///     Collects owned managed-database metrics and durable log checkpoints separately from web runtime supervision.
/// </summary>
/// <param name="databaseClient">Reads verified ownership tags and native database metrics.</param>
/// <param name="monitorLogsClient">Reads bounded database operational logs.</param>
/// <param name="diagnostics">Confirms redacted durable writes before checkpoint advancement.</param>
/// <param name="scopeFactory">Creates independent checkpoint contexts for each source read.</param>
/// <param name="options">Retains the current overlap, batch and page-limit settings.</param>
/// <param name="availability">Shares source outage/recovery deduplication with the runtime coordinator.</param>
internal sealed class AzureDatabaseDiagnostics(
    AzureManagedDatabaseClient databaseClient,
    AzureMonitorLogsClient monitorLogsClient,
    IDeploymentDiagnosticPublisher diagnostics,
    IServiceScopeFactory scopeFactory,
    IOptions<AzureMonitorLogsOptions> options,
    AzureRuntimeAvailability availability)
{
    /// <summary>
    ///     Collects each managed database independently so a database failure cannot suppress web
    ///     diagnostics.
    /// </summary>
    /// <param name="target">Verified project, deployment and provider resource inventory.</param>
    /// <param name="logsToken">Memory-only Azure Monitor data-plane access token.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    internal async Task PollAsync(ContainerAppStreamTarget target, string? logsToken,
        CancellationToken cancellationToken)
    {
        if (!target.CollectDatabases) return;
        if (logsToken is null)
            try
            {
                target.Databases = await databaseClient.DiscoverAsync(target.ResourceId, target.ProjectId,
                    target.ContainerAppName, target.AzureCredentials.AccessToken, cancellationToken,
                    target.DatabaseContainers);
                if (target.DatabaseProviders is { } providers)
                    target.Databases = target.Databases.Where(database => providers.Contains(database.Provider))
                        .ToArray();
                availability.Clear(target, "database-inventory");
                if (target.DatabaseContainers.Count > 0 && target.Databases.Count == 0)
                    await availability.ReportAsync(target, "database-inventory-empty",
                        "No owned managed database resources were found. Database containers use Container Apps logs and shared app metrics; managed services require both AutoMate ownership tags.",
                        cancellationToken);
                else availability.Clear(target, "database-inventory-empty");
            }
            catch (Exception ex) when (ex is not OperationCanceledException ||
                                       !cancellationToken.IsCancellationRequested)
            {
                await availability.ReportAsync(target, "database-inventory",
                    "Azure database discovery is unavailable. Verify resource-group read access and AutoMate resource tags.",
                    cancellationToken);
                return;
            }

        foreach (var database in target.Databases)
        {
            var key = "db-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(database.ResourceId)))[..24];
            var issue = key + (logsToken is null ? "-metrics" : "-logs");
            try
            {
                if (logsToken is null)
                {
                    if (target.MetricsDue) await PublishDatabaseMetricsAsync(target, database, cancellationToken);
                }
                else if (database.HasOperationalLogs)
                {
                    await TailDatabaseAsync(target, database, key, logsToken, cancellationToken);
                }
                else
                {
                    await availability.ReportAsync(target, key + "-metrics-only",
                        "This managed database provides metrics only. Audit and slow-query logs are not enabled by AutoMate.",
                        cancellationToken);
                }

                if (logsToken is null) availability.Clear(target, issue);
            }
            catch (Exception ex) when (ex is not OperationCanceledException ||
                                       !cancellationToken.IsCancellationRequested)
            {
                await availability.ReportAsync(target, issue,
                    "Azure database diagnostics are temporarily unavailable. Verify Monitor permissions and diagnostic settings.",
                    cancellationToken);
            }
        }
    }

    /// <summary>
    ///     Publishes native database gauges through the same redacted live and durable metric
    ///     pipeline.
    /// </summary>
    /// <param name="target">Verified project, deployment and provider resource inventory.</param>
    /// <param name="database">Selected owned database resource or deployment configuration.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private async Task PublishDatabaseMetricsAsync(ContainerAppStreamTarget target, AzureManagedDatabase database,
        CancellationToken cancellationToken)
    {
        var observations = await DeploymentTracing.RunAsync(DeploymentOperation.AzureMetrics, target.ProjectId,
            target.DeploymentId, cancellationToken,
            () => databaseClient.MetricObservationsAsync(database, target.AzureCredentials.AccessToken,
                cancellationToken));
        var emptyIssue = "db-" +
                         Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(database.ResourceId)))[..24] +
                         "-metrics-empty";
        if (observations.Count == 0)
            await availability.ReportAsync(target, emptyIssue,
                "Azure returned no database metric samples in the recent window. Samples can be delayed or unavailable for this resource tier.",
                cancellationToken);
        else availability.Clear(target, emptyIssue);
        foreach (var observation in observations)
        {
            var samples = observation.Samples;
            var cpu = samples.FirstOrDefault(s => s.Name is "automate_cpu_usage_percent" or "automate_cpu_usage_cores");
            var memory =
                samples.FirstOrDefault(s => s.Name.StartsWith("automate_memory_used_", StringComparison.Ordinal));
            await diagnostics.PublishAsync(new DeploymentDiagnosticEvent(target.ProjectId, target.DeploymentId,
                DeploymentDiagnosticSource.AzureContainerApps, DeploymentDiagnosticKind.Metric,
                DeploymentDiagnosticSeverity.Information, observation.Timestamp,
                $"Azure database metrics: {database.Name}.",
                new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Metrics, database.Name),
                new Dictionary<string, string>
                {
                    ["cpu"] = cpu is null ? "n/a" :
                        cpu.Unit == "cores" ? $"{cpu.Value:0.##} cores" : $"{cpu.Value:0.##}%",
                    ["memory"] = memory is null ? "n/a" :
                        memory.Unit == "bytes" ? $"{memory.Value / 1048576:0.##} MiB" : $"{memory.Value:0.##}%"
                }, SourceIdentity: new DeploymentDiagnosticSourceIdentity(DeploymentDiagnosticComponent.Database,
                    DeploymentDiagnosticStream.Metric, database.ResourceId), Metrics: samples,
                EventId: AzureRuntimeDiagnosticPolicy.MetricEventId(target,
                    database.ResourceId + database.ContainerAppRevision, observation)), cancellationToken);
        }
    }

    /// <summary>
    ///     Advances one database cursor only for confirmed writes, preserving retry identity across
    ///     restart.
    /// </summary>
    /// <param name="target">Verified project, deployment and provider resource inventory.</param>
    /// <param name="database">Selected owned database resource or deployment configuration.</param>
    /// <param name="key">Stable lookup or configuration key.</param>
    /// <param name="logsToken">Memory-only Azure Monitor data-plane access token.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private async Task TailDatabaseAsync(ContainerAppStreamTarget target, AzureManagedDatabase database, string key,
        string logsToken, CancellationToken cancellationToken)
    {
        if (database.ContainerAppRevision is not null && database.LogScope is null)
        {
            await availability.ReportAsync(target, key + "-logs",
                "Associated database Container App log environment is unavailable. Verify Azure read access.",
                cancellationToken);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var store = new AzureContainerAppLogCheckpointStore(
            scope.ServiceProvider.GetRequiredService<AutoMateDbContext>());
        var checkpoint = await store.GetOrCreateAsync(target.DeploymentId, key, cancellationToken);
        var from = (checkpoint.LastIngestionTimestamp ??
                    checkpoint.LastTimestamp ?? DateTimeOffset.UtcNow - TimeSpan.FromSeconds(
                        Math.Clamp(options.Value.DatabaseInitialLookback.TotalSeconds, 60, 7 * 24 * 60 * 60))) -
                   options.Value.OverlapWindow;
        for (var page = 0; page < Math.Clamp(options.Value.MaximumPagesPerPoll, 1, 100); page++)
        {
            var result = await DeploymentTracing.RunAsync(DeploymentOperation.AzureLogs, target.ProjectId,
                target.DeploymentId, cancellationToken,
                () => database.ContainerAppRevision is { } revision
                    ? monitorLogsClient.QueryAsync(database.LogScope!.ResourceId, logsToken,
                        AzureContainerAppLogSource.Console,
                        database.Name, from, options.Value.BatchSize, cancellationToken, revision,
                        checkpoint.LastIngestionTimestamp, checkpoint.LastIngestionTieBreaker,
                        database.LogScope.EnvironmentName, database.LogScope.ResourceName)
                    : monitorLogsClient.QueryDatabaseAsync(database.ResourceId, logsToken, from,
                        options.Value.BatchSize, checkpoint.LastIngestionTimestamp, checkpoint.LastIngestionTieBreaker,
                        cancellationToken),
                result => result.IsSuccess);
            if (!result.IsSuccess)
            {
                await availability.ReportAsync(target, key + "-logs", result.FailureReason!, cancellationToken);
                break;
            }

            availability.Clear(target, key + "-logs");
            if (result.Records.Count == 0 && checkpoint.LastTimestamp is null)
                await availability.ReportAsync(target, key + "-logs-empty",
                    "No database operational logs are available yet. Verify diagnostic export to Log Analytics; an idle database may not emit events and ingestion can take several minutes.",
                    cancellationToken);
            else availability.Clear(target, key + "-logs-empty");
            var confirmed = true;
            foreach (var record in result.Records.OrderBy(r => r.IngestionTimestampUtc ?? r.TimestampUtc)
                         .ThenBy(AzureRuntimeDiagnosticPolicy.CreateTieBreaker, StringComparer.Ordinal))
            {
                var hash = AzureRuntimeDiagnosticPolicy.CreateTieBreaker(record);
                if (!AzureRuntimeDiagnosticPolicy.IsAfterCheckpoint(record, hash, checkpoint)) continue;
                var diagnostic = new DeploymentDiagnosticEvent(target.ProjectId, target.DeploymentId,
                    DeploymentDiagnosticSource.AzureContainerApps, DeploymentDiagnosticKind.Log,
                    DeploymentDiagnosticSeverity.Information, record.TimestampUtc,
                    $"[{database.Name}] {record.Message}",
                    new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, "azure-db"),
                    SourceIdentity: new DeploymentDiagnosticSourceIdentity(DeploymentDiagnosticComponent.Database,
                        database.ContainerAppRevision is null
                            ? DeploymentDiagnosticStream.System
                            : string.Equals(record.Stream, "stderr", StringComparison.OrdinalIgnoreCase)
                                ? DeploymentDiagnosticStream.StandardError
                                : DeploymentDiagnosticStream.StandardOutput,
                        database.ResourceId),
                    EventId: new Guid(
                        SHA256.HashData(Encoding.UTF8.GetBytes($"{target.DeploymentId:N}/{key}/{hash}"))[..16]));
                if (diagnostics is not IDurableDeploymentDiagnosticPublisher durable ||
                    !await durable.PublishDurablyAsync(diagnostic, cancellationToken))
                {
                    confirmed = false;
                    break;
                }

                checkpoint.LastTimestamp = record.TimestampUtc;
                checkpoint.LastTieBreaker = hash;
                checkpoint.LastIngestionTimestamp = record.IngestionTimestampUtc ?? record.TimestampUtc;
                checkpoint.LastIngestionTieBreaker = hash;
            }

            checkpoint.LastSuccessfulQueryAt = DateTimeOffset.UtcNow;
            await store.SaveAsync(cancellationToken);
            if (!confirmed || result.Records.Count < options.Value.BatchSize) break;
        }
    }
}
