using System.Security.Cryptography;
using System.Text;
using Application.Abstractions.Diagnostics.Events;
using Domain.Entities.Diagnostics;
using Infrastructure.Azure.Monitor;

namespace Infrastructure.Azure.ContainerApps.Runtime;

/// <summary>
///     Maps Azure source identity, event hashes and checkpoint ordering independently of polling and scope lifetimes.
/// </summary>
internal static class AzureRuntimeDiagnosticPolicy
{
    /// <summary>The existing terminal identity for managed web output.</summary>
    private const string CloudWebContainerName = "cloud-web";

    /// <summary>
    ///     Compares ingestion timestamp and stable hash against the last durably confirmed source
    ///     cursor.
    /// </summary>
    /// <param name="record">Normalized Azure event with its original timestamp and ingestion cursor.</param>
    /// <param name="tieBreaker">Stable hash ordering observations with equal timestamps.</param>
    /// <param name="checkpoint">Durable source position used to avoid duplicate delivery.</param>
    /// <returns>Whether the documented after checkpoint condition holds.</returns>
    internal static bool IsAfterCheckpoint(AzureMonitorLogRecord record, string tieBreaker,
        AzureContainerAppLogCheckpoint checkpoint)
    {
        var previous = record.IngestionTimestampUtc is null
            ? checkpoint.LastTimestamp
            : checkpoint.LastIngestionTimestamp;
        var hash = record.IngestionTimestampUtc is null
            ? checkpoint.LastTieBreaker
            : checkpoint.LastIngestionTieBreaker;

        if (previous is null) return true;

        var timestampComparison = (record.IngestionTimestampUtc ?? record.TimestampUtc)
            .CompareTo(previous.Value);

        return timestampComparison > 0
               || (timestampComparison == 0
                   && string.CompareOrdinal(tieBreaker, hash) > 0);
    }

    /// <summary>
    ///     Gives an Azure metric minute a stable retry identity independent of polling time.
    /// </summary>
    /// <param name="target">Verified project, deployment and provider resource inventory.</param>
    /// <param name="resource">Verified provider resource identity.</param>
    /// <param name="observation">
    ///     Typed Azure resource observation preserving timestamp, units and aggregations.
    /// </param>
    /// <returns>The resolved internal identity.</returns>
    internal static Guid MetricEventId(ContainerAppStreamTarget target, string resource,
        AzureMetricObservation observation)
    {
        return new Guid(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{target.DeploymentId:N}/{resource}/{observation.Timestamp:O}"))[..16]);
    }

    /// <summary>
    ///     Derives a stable content identity for records sharing a provider timestamp.
    /// </summary>
    /// <param name="record">Normalized Azure event with its original timestamp and ingestion cursor.</param>
    /// <returns>The stable identity derived from the documented metadata or content.</returns>
    internal static string CreateTieBreaker(AzureMonitorLogRecord record)
    {
        if (record.CursorHash is not null) return record.CursorHash;
        var payload = string.Join("\n", record.TimestampUtc.ToString("O"), record.SourceTable, record.Message,
            record.ContainerName, record.RevisionName, record.Stream);

        return Convert
            .ToHexString(SHA256
                .HashData(Encoding.UTF8
                    .GetBytes(payload)))
            .ToLowerInvariant();
    }

    /// <summary>
    ///     Projects an Azure record into a deployment-correlated terminal diagnostic.
    /// </summary>
    /// <param name="target">Verified project, deployment and provider resource inventory.</param>
    /// <param name="source">Verified source classification, resource identity or owned input stream.</param>
    /// <param name="record">Normalized Azure event with its original timestamp and ingestion cursor.</param>
    /// <returns>The normalized deployment-correlated observation.</returns>
    internal static DeploymentDiagnosticEvent CreateLogEvent(ContainerAppStreamTarget target,
        AzureContainerAppLogSource source, AzureMonitorLogRecord record)
    {
        var isConsole = source == AzureContainerAppLogSource.Console;
        var isDatabase = isConsole && target.DatabaseContainers.Contains(record.ContainerName);

        var attributes = new Dictionary<string, string>
        {
            ["source_table"] = record.SourceTable,
            ["stream"] = record.Stream,
            ["revision"] = record.RevisionName,
            ["container"] = record.ContainerName,
            ["replica"] = record.ReplicaName
        };

        return new DeploymentDiagnosticEvent(target.ProjectId, target.DeploymentId,
            DeploymentDiagnosticSource.AzureContainerApps, DeploymentDiagnosticKind.Log,
            isConsole ? DeploymentDiagnosticSeverity.Information : DeploymentDiagnosticSeverity.Warning,
            record.TimestampUtc, isConsole
                ? record.Message
                : $"[Azure system] {record.Message}",
            isConsole
                ? new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container,
                    isDatabase ? "azure-db" : CloudWebContainerName)
                : new DeploymentTerminalChannel(DeploymentTerminalChannelKind.System), attributes,
            Cursor: CreateTieBreaker(record),
            SourceIdentity: new DeploymentDiagnosticSourceIdentity(
                isDatabase
                    ? DeploymentDiagnosticComponent.Database
                    : isConsole
                        ? DeploymentDiagnosticComponent.Container
                        : DeploymentDiagnosticComponent.Revision,
                isConsole && string.Equals(record.Stream, "stderr", StringComparison.OrdinalIgnoreCase)
                    ? DeploymentDiagnosticStream.StandardError
                    : isConsole
                        ? DeploymentDiagnosticStream.StandardOutput
                        : DeploymentDiagnosticStream.System,
                isConsole
                    ? record.ContainerName
                    : record.RevisionName));
    }
}
