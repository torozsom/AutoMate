using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Application.Abstractions.Azure;
using Application.Abstractions.Diagnostics;
using Domain.DTO;
using Domain.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Azure;

/// <summary>Host-managed coordinator for Azure Container Apps state, metrics, console output, and system events.</summary>
public sealed class AzureContainerAppRuntimeStreamer(
    IDeploymentDiagnosticPublisher diagnostics,
    IAzureMonitorLogsTokenProvider monitorTokenProvider,
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<AzureMonitorLogsOptions> options,
    ILogger<AzureContainerAppRuntimeStreamer> logger) : BackgroundService, IAzureContainerAppRuntimeStreamer
{
    private const string CloudWebContainerName = "cloud-web";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private readonly AzureContainerAppClient _containerAppClient = new(httpClientFactory);
    private readonly AzureMonitorLogsClient _monitorLogsClient = new(httpClientFactory);
    private readonly ConcurrentDictionary<Guid, ContainerAppStreamTarget> _targets = new();
    private readonly ConcurrentDictionary<string, string> _reportedIssues = new();

    /// <inheritdoc />
    public void StartStreaming(AzureContainerAppRuntimeStreamRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryCreateStreamTarget(request, out var target))
        {
            logger.LogWarning("Azure runtime monitoring was not registered for deployment {DeploymentId}: incomplete configuration.",
                request.DeploymentId);
            return;
        }

        _targets.AddOrUpdate(target.ProjectId, target, (_, _) => target);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PollAllAsync(stoppingToken);
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await PollAllAsync(stoppingToken);
    }

    private async Task PollAllAsync(CancellationToken cancellationToken)
    {
        foreach (var target in _targets.Values)
        {
            try
            {
                await PollTargetAsync(target, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Azure runtime monitoring failed for deployment {DeploymentId}.", target.DeploymentId);
                await PublishIssueOnceAsync(target, "coordinator", "Azure runtime monitoring encountered a recoverable error.",
                    cancellationToken);
            }
        }
    }

    private async Task PollTargetAsync(ContainerAppStreamTarget target, CancellationToken cancellationToken)
    {
        await PollStateAndMetricsAsync(target, cancellationToken);

        var tokenResult = await monitorTokenProvider.GetTokenAsync(target.UserId, cancellationToken);
        if (!tokenResult.IsSuccess)
        {
            await PublishIssueOnceAsync(target, "token", tokenResult.FailureReason!, cancellationToken);
            return;
        }

        ClearIssue(target, "token");
        await using var scope = scopeFactory.CreateAsyncScope();
        var checkpoints = new AzureContainerAppLogCheckpointStore(
            scope.ServiceProvider.GetRequiredService<Infrastructure.Data.AutoMateDbContext>());
        await TailSourceAsync(target, AzureContainerAppLogSource.Console, tokenResult.AccessToken!, checkpoints,
            cancellationToken);
        await TailSourceAsync(target, AzureContainerAppLogSource.System, tokenResult.AccessToken!, checkpoints,
            cancellationToken);
    }

    private async Task PollStateAndMetricsAsync(ContainerAppStreamTarget target, CancellationToken cancellationToken)
    {
        var state = await _containerAppClient.GetStateAsync(target.ResourceId, target.AzureCredentials.AccessToken,
            cancellationToken);
        if (state != null && (state.LatestRevision != target.LastRevision || state.Fqdn != target.LastFqdn))
        {
            target.LastRevision = state.LatestRevision;
            target.LastFqdn = state.Fqdn;
            await diagnostics.PublishAsync(new DeploymentDiagnosticEvent(target.ProjectId, target.DeploymentId,
                DeploymentDiagnosticSource.AzureContainerApps, DeploymentDiagnosticKind.Lifecycle,
                DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow, CreateAvailabilityMessage(state),
                new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, CloudWebContainerName),
                new Dictionary<string, string> { ["revision"] = state.LatestRevision }), cancellationToken);
        }

        var metrics = await _containerAppClient.GetMetricsAsync(target.ResourceId, target.AzureCredentials.AccessToken,
            cancellationToken);
        if (metrics != null)
            await diagnostics.PublishAsync(new DeploymentDiagnosticEvent(target.ProjectId, target.DeploymentId,
                DeploymentDiagnosticSource.AzureContainerApps, DeploymentDiagnosticKind.Metric,
                DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow,
                $"Azure Container Apps metrics: CPU {metrics.Cpu}, memory {metrics.Memory}.",
                new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Metrics, CloudWebContainerName),
                new Dictionary<string, string> { ["cpu"] = metrics.Cpu, ["memory"] = metrics.Memory }),
                cancellationToken);
    }

    private async Task TailSourceAsync(ContainerAppStreamTarget target, AzureContainerAppLogSource source,
        string accessToken, AzureContainerAppLogCheckpointStore checkpoints, CancellationToken cancellationToken)
    {
        var sourceName = source.ToString().ToLowerInvariant();
        var checkpoint = await checkpoints.GetOrCreateAsync(target.DeploymentId, sourceName, cancellationToken);
        var settings = options.Value;
        var from = (checkpoint.LastTimestamp ?? DateTimeOffset.UtcNow.Subtract(settings.InitialLookback))
            .Subtract(settings.OverlapWindow);
        var result = await _monitorLogsClient.QueryAsync(target.ResourceId, accessToken, source,
            target.ContainerAppName, from, settings.BatchSize, cancellationToken);
        if (!result.IsSuccess)
        {
            await PublishIssueOnceAsync(target, sourceName, result.FailureReason!, cancellationToken);
            return;
        }

        ClearIssue(target, sourceName);
        foreach (var record in result.Records.OrderBy(item => item.TimestampUtc).ThenBy(CreateTieBreaker,
                     StringComparer.Ordinal))
        {
            var tieBreaker = CreateTieBreaker(record);
            if (!IsAfterCheckpoint(record.TimestampUtc, tieBreaker, checkpoint)) continue;

            await diagnostics.PublishAsync(CreateLogEvent(target, source, record), cancellationToken);
            checkpoint.LastTimestamp = record.TimestampUtc;
            checkpoint.LastTieBreaker = tieBreaker;

            if (DateTimeOffset.UtcNow - record.TimestampUtc > settings.FreshnessWarningAge)
                await PublishIssueOnceAsync(target, $"{sourceName}-freshness",
                    $"Azure {sourceName} log ingestion is delayed; newest delivered record is {(DateTimeOffset.UtcNow - record.TimestampUtc).TotalMinutes:0} minute(s) old.",
                    cancellationToken);
            else
                ClearIssue(target, $"{sourceName}-freshness");
        }

        checkpoint.LastSuccessfulQueryAt = DateTimeOffset.UtcNow;
        await checkpoints.SaveAsync(cancellationToken);
    }

    private static DeploymentDiagnosticEvent CreateLogEvent(ContainerAppStreamTarget target,
        AzureContainerAppLogSource source, AzureMonitorLogRecord record)
    {
        var isConsole = source == AzureContainerAppLogSource.Console;
        var attributes = new Dictionary<string, string>
        {
            ["source_table"] = record.SourceTable, ["stream"] = record.Stream,
            ["revision"] = record.RevisionName, ["container"] = record.ContainerName
        };
        return new DeploymentDiagnosticEvent(target.ProjectId, target.DeploymentId,
            DeploymentDiagnosticSource.AzureContainerApps, DeploymentDiagnosticKind.Log,
            isConsole ? DeploymentDiagnosticSeverity.Information : DeploymentDiagnosticSeverity.Warning,
            record.TimestampUtc, isConsole ? record.Message : $"[Azure system] {record.Message}",
            isConsole
                ? new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, CloudWebContainerName)
                : new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Build), attributes,
            Cursor: CreateTieBreaker(record));
    }

    private async Task PublishIssueOnceAsync(ContainerAppStreamTarget target, string issue, string message,
        CancellationToken cancellationToken)
    {
        var key = $"{target.DeploymentId:N}:{issue}";
        if (!_reportedIssues.TryAdd(key, message)) return;
        await diagnostics.PublishAsync(new DeploymentDiagnosticEvent(target.ProjectId, target.DeploymentId,
            DeploymentDiagnosticSource.AzureContainerApps, DeploymentDiagnosticKind.Annotation,
            DeploymentDiagnosticSeverity.Warning, DateTimeOffset.UtcNow, $"[Azure runtime] {message}",
            new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Build),
            new Dictionary<string, string> { ["issue"] = issue }), cancellationToken);
    }

    private void ClearIssue(ContainerAppStreamTarget target, string issue) =>
        _reportedIssues.TryRemove($"{target.DeploymentId:N}:{issue}", out _);

    private static bool IsAfterCheckpoint(DateTimeOffset timestamp, string tieBreaker,
        AzureContainerAppLogCheckpoint checkpoint)
    {
        if (checkpoint.LastTimestamp is null) return true;
        var timestampComparison = timestamp.CompareTo(checkpoint.LastTimestamp.Value);
        return timestampComparison > 0 || timestampComparison == 0 &&
            string.CompareOrdinal(tieBreaker, checkpoint.LastTieBreaker) > 0;
    }

    private static string CreateTieBreaker(AzureMonitorLogRecord record)
    {
        var payload = string.Join("\n", record.TimestampUtc.ToString("O"), record.SourceTable, record.Message,
            record.ContainerName, record.RevisionName, record.Stream);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static bool TryCreateStreamTarget(AzureContainerAppRuntimeStreamRequest request,
        out ContainerAppStreamTarget target)
    {
        target = null!;
        if (request.ProjectId == Guid.Empty || request.DeploymentId == Guid.Empty || request.UserId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.AzureCredentials.AccessToken) ||
            string.IsNullOrWhiteSpace(request.AzureCredentials.SubscriptionId) ||
            string.IsNullOrWhiteSpace(request.Config.CloudResourceGroupName) ||
            string.IsNullOrWhiteSpace(request.Config.CloudContainerAppName)) return false;

        var resourceId = $"/subscriptions/{Uri.EscapeDataString(request.AzureCredentials.SubscriptionId)}/resourceGroups/{Uri.EscapeDataString(request.Config.CloudResourceGroupName)}/providers/Microsoft.App/containerApps/{Uri.EscapeDataString(request.Config.CloudContainerAppName)}";
        target = new ContainerAppStreamTarget(request.ProjectId, request.DeploymentId, request.UserId, resourceId,
            request.Config.CloudContainerAppName, request.AzureCredentials);
        return true;
    }

    private static string CreateAvailabilityMessage(AzureContainerAppState state) =>
        $"Azure Container App is available{(string.IsNullOrWhiteSpace(state.Fqdn) ? string.Empty : $" at https://{state.Fqdn}")}. Latest ready revision: {state.LatestRevision}.";

    private sealed class ContainerAppStreamTarget(Guid projectId, Guid deploymentId, Guid userId, string resourceId,
        string containerAppName, AzureCloudCredentialsDto azureCredentials)
    {
        public Guid ProjectId { get; } = projectId;
        public Guid DeploymentId { get; } = deploymentId;
        public Guid UserId { get; } = userId;
        public string ResourceId { get; } = resourceId;
        public string ContainerAppName { get; } = containerAppName;
        public AzureCloudCredentialsDto AzureCredentials { get; } = azureCredentials;
        public string LastRevision { get; set; } = string.Empty;
        public string LastFqdn { get; set; } = string.Empty;
    }
}
