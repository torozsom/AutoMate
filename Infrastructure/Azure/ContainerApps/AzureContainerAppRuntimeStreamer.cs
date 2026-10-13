using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Abstractions.Azure;
using Application.Abstractions.Diagnostics;
using Application.Diagnostics;
using Domain.DTO;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Azure;

/// <summary>
///     Host-managed coordinator for Azure Container Apps state, metrics, console output, and
///     system events.
/// </summary>
/// <param name="diagnostics">Accepts normalized observations for redaction and durable delivery.</param>
/// <param name="monitorTokenProvider">
///     Provides the azure monitor logs token provider boundary used by this operation.
/// </param>
/// <param name="httpClientFactory">Configured HTTP transport used for bounded provider requests.</param>
/// <param name="scopeFactory">Creates independent scopes for concurrent background work.</param>
/// <param name="options">Provides the configured limits and endpoint settings for this service.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
/// <param name="telemetry">Provides the configured limits and endpoint settings for this service.</param>
public sealed class AzureContainerAppRuntimeStreamer(
    IDeploymentDiagnosticPublisher diagnostics,
    IAzureMonitorLogsTokenProvider monitorTokenProvider,
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<AzureMonitorLogsOptions> options,
    ILogger<AzureContainerAppRuntimeStreamer> logger,
    IOptions<TelemetryStorageOptions>? telemetry = null) : BackgroundService, IAzureContainerAppRuntimeStreamer
{
    /// <summary>Stable resource identity used by compatibility web metric delivery.</summary>
    private const string CloudWebContainerName = "cloud-web";

    /// <summary>Host-managed Azure observation cadence, independent of browser presence.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    /// <summary>Deduplicates current finite source-availability notices.</summary>
    private readonly AzureRuntimeAvailability _availability = new(diagnostics);

    /// <summary>Reads verified Container App state and metrics through bounded ARM requests.</summary>
    private readonly AzureContainerAppClient _containerAppClient = new(httpClientFactory);

    /// <summary>Reads tagged managed databases independently of Container Apps logs.</summary>
    private readonly AzureManagedDatabaseClient _databaseClient = new(httpClientFactory);

    /// <summary>Per-host fencing identity used to acquire runtime collector leases.</summary>
    private readonly Guid _leaseOwner = Guid.NewGuid();

    /// <summary>Long-lived ACA HTTP readers are independent of the finite polling deadline.</summary>
    private readonly AzureContainerAppLiveLogs _liveLogs = new(httpClientFactory, diagnostics, logger);

    /// <summary>Queries bounded verified console, system and managed-database history.</summary>
    private readonly AzureMonitorLogsClient _monitorLogsClient = new(httpClientFactory);

    /// <summary>Verified active deployment targets supervised by this host.</summary>
    private readonly ConcurrentDictionary<Guid, ContainerAppStreamTarget> _targets = new();

    /// <summary>Live sessions start only while this coordinator is hosted and stop with its lifetime.</summary>
    private CancellationToken? _liveLifetime;

    /// <summary>
    ///     Starts background streaming for a cloud deployment's Container App.
    /// </summary>
    /// <param name="request">The verified deployment resource and revision to supervise.</param>
    /// <exception cref="ArgumentNullException">A required input or dependency is null.</exception>
    public void StartStreaming(AzureContainerAppRuntimeStreamRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryCreateStreamTarget(request, out var target))
        {
            logger.LogWarning(
                "Azure runtime monitoring was not registered for deployment {DeploymentId}: incomplete configuration.",
                request.DeploymentId);
            return;
        }

        _targets.AddOrUpdate(target.DeploymentId, target, (_, _) => target);
    }

    /// <summary>
    ///     Supervises automatic verified Azure runtime collection and independent source recovery.
    /// </summary>
    /// <param name="stoppingToken">Signals host shutdown.</param>
    /// <returns>A task representing the supervised service lifetime and cleanup.</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Azure Container Apps runtime monitoring coordinator started.");
        _liveLifetime = stoppingToken;
        try
        {
            await RecoverTargetsAsync(stoppingToken);
            await PollOnceAsync(stoppingToken);
            using var timer =
                new PeriodicTimer(
                    TimeSpan.FromSeconds(Math.Min(60,
                        telemetry?.Value.RuntimeSampleSeconds ?? PollInterval.TotalSeconds)));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RecoverTargetsAsync(stoppingToken);
                await PollOnceAsync(stoppingToken);
            }
        }
        finally
        {
            _liveLifetime = null;
            await _liveLogs.StopAsync();
            logger.LogInformation("Azure Container Apps runtime monitoring coordinator stopped.");
        }
    }

    /// <summary>
    ///     Processes each registered target once. Internal for deterministic collector
    ///     verification.
    /// </summary>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(_targets.Values, new ParallelOptions
        {
            MaxDegreeOfParallelism = 8,
            CancellationToken = cancellationToken
        }, async (target, ct) =>
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(45));
                await DeploymentTracing.RunAsync(DeploymentOperation.AzurePoll, target.ProjectId, target.DeploymentId,
                    ct, () => PollTargetAsync(target, deadline.Token));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError("Azure runtime monitoring failed for deployment {DeploymentId}. Failure {FailureType}.",
                    target.DeploymentId, ex.GetType().Name);
                await _availability.ReportAsync(target, "coordinator",
                    "Azure runtime monitoring encountered a recoverable error.",
                    ct);
            }
        });
    }

    /// <summary>
    ///     Collects verified runtime state, logs and metrics while isolating independent source
    ///     failures.
    /// </summary>
    /// <param name="target">Verified project, deployment and provider resource inventory.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private async Task PollTargetAsync(ContainerAppStreamTarget target, CancellationToken cancellationToken)
    {
        await using var policyScope = scopeFactory.CreateAsyncScope();
        var project = await policyScope.ServiceProvider.GetRequiredService<AutoMateDbContext>().Applications
            .Where(p => p.Id == target.ProjectId && p.UserId == target.UserId)
            .Select(p => new { p.Id }).SingleOrDefaultAsync(cancellationToken);
        if (project is null)
        {
            _targets.TryRemove(target.DeploymentId, out _);
            _liveLogs.Remove(target.DeploymentId);
            return;
        }

        var db = policyScope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
        var deployment = await db.Deployments.SingleOrDefaultAsync(d => d.Id == target.DeploymentId &&
                                                                        d.CsProject!.AppId == target.ProjectId,
            cancellationToken);
        if (deployment is null || deployment.Status is DeploymentStatus.Stopped or DeploymentStatus.Failed)
        {
            _targets.TryRemove(target.DeploymentId, out _);
            _liveLogs.Remove(target.DeploymentId);
            return;
        }

        if (deployment.CloudResourceId is null)
        {
            deployment.CloudResourceId = target.ResourceId;
            deployment.CloudContainerAppName = target.ContainerAppName;
            deployment.CloudContainerRevision ??= target.ExpectedRevision;
            await db.SaveChangesAsync(cancellationToken);
        }

        target.ExpectedRevision ??= deployment.CloudContainerRevision;
        if (deployment.CloudResourceId != target.ResourceId ||
            (deployment.CloudContainerRevision is { } verifiedRevision && verifiedRevision != target.ExpectedRevision))
        {
            _targets.TryRemove(target.DeploymentId, out _);
            _liveLogs.Remove(target.DeploymentId);
            return;
        }

        if (deployment.ConfigurationSnapshotJson is { } configuration)
        {
            var snapshot = JsonSerializer.Deserialize<DeploymentConfigurationSnapshot>(configuration);
            target.DatabaseProviders = snapshot?.Databases?.Select(d => d.Provider)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            target.DatabaseContainers = (snapshot?.Databases ?? [])
                .Select(d => d.ContainerSuffix).Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.Ordinal);
        }

        var active = await db.Deployments.Where(d => d.Status == DeploymentStatus.Running &&
                                                     d.CsProject!.AppId == target.ProjectId &&
                                                     d.CloudResourceId == target.ResourceId)
            .Select(d => new { d.Id, d.CreatedAt }).Take(1001).ToArrayAsync(cancellationToken);
        target.CollectDatabases = active.Length <= 1000 && active.OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id).FirstOrDefault()?.Id == target.DeploymentId;
        var now = DateTimeOffset.UtcNow;
        target.MetricsDue = now >= target.NextMetricsAt;
        if (target.MetricsDue)
            target.NextMetricsAt = now.AddSeconds(telemetry?.Value.RuntimeSampleSeconds ?? PollInterval.TotalSeconds);
        if (db.Database.IsNpgsql())
        {
            var claimed = await db.Deployments.Where(d => d.Id == target.DeploymentId &&
                                                          (d.RuntimeCollectorLeaseOwner == _leaseOwner ||
                                                           d.RuntimeCollectorLeaseUntil == null ||
                                                           d.RuntimeCollectorLeaseUntil <= now))
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.RuntimeCollectorLeaseOwner, _leaseOwner)
                    .SetProperty(d => d.RuntimeCollectorLeaseUntil, now.AddMinutes(2)), cancellationToken);
            if (claimed == 0)
            {
                _liveLogs.Remove(target.DeploymentId);
                return;
            }
        }

        if (policyScope.ServiceProvider.GetService<AzureArmCredentialsProvider>() is { } credentials)
            target.AzureCredentials = await credentials.GetAsync(target.UserId, cancellationToken);
        if (_liveLifetime is { } lifetime && !string.IsNullOrWhiteSpace(target.ExpectedRevision))
            _liveLogs.Update(new AzureLiveLogTarget(target.ProjectId, target.DeploymentId, target.ResourceId,
                target.ExpectedRevision, target.AzureCredentials.AccessToken, now.AddSeconds(110),
                target.CollectDatabases, target.DatabaseContainers), lifetime);
        // Inventory and native database metrics must complete before potentially slow history queries.
        await PollDatabasesAsync(target, null, cancellationToken);
        try
        {
            await PollStateAndMetricsAsync(target, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await _availability.ReportAsync(target, "metrics",
                "Azure Container Apps state or metrics query failed; log collection will continue.", cancellationToken);
        }

        var tokenResult = await monitorTokenProvider.GetTokenAsync(target.UserId, cancellationToken);
        if (!tokenResult.IsSuccess)
        {
            await _availability.ReportAsync(target, "token", tokenResult.FailureReason!, cancellationToken);
            return;
        }

        _availability.Clear(target, "token");

        await using var scope = scopeFactory.CreateAsyncScope();
        var checkpoints = new AzureContainerAppLogCheckpointStore(
            scope.ServiceProvider.GetRequiredService<AutoMateDbContext>());
        // Database history owns independent checkpoint scopes and cannot be starved by ACA history.
        var databaseLogs = PollDatabasesAsync(target, tokenResult.AccessToken, cancellationToken);
        try
        {
            foreach (var source in new[] { AzureContainerAppLogSource.Console, AzureContainerAppLogSource.System })
                try
                {
                    await TailSourceAsync(target, source, tokenResult.AccessToken!, checkpoints, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException ||
                                           !cancellationToken.IsCancellationRequested)
                {
                    await _availability.ReportAsync(target, source.ToString().ToLowerInvariant(),
                        "Azure log query failed; collection will retry without advancing its checkpoint.",
                        cancellationToken);
                }
        }
        finally
        {
            await databaseLogs;
        }
    }

    /// <summary>
    ///     Resolves the environment log scope and publishes only observed, revision-scoped web
    ///     metrics.
    /// </summary>
    /// <param name="target">Verified project, deployment and provider resource inventory.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private async Task PollStateAndMetricsAsync(ContainerAppStreamTarget target, CancellationToken cancellationToken)
    {
        AzureContainerAppState? state = null;
        try
        {
            state = await _containerAppClient.GetStateAsync(target.ResourceId, target.AzureCredentials.AccessToken,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(state?.ManagedEnvironmentId))
            {
                var logScope = await _containerAppClient.GetEnvironmentLogScopeAsync(state.ManagedEnvironmentId,
                    target.AzureCredentials.AccessToken, cancellationToken);
                if (logScope is not null)
                {
                    target.LogResourceId = logScope.ResourceId;
                    target.LogEnvironmentName = logScope.EnvironmentName;
                    target.LogEnvironmentResourceName = logScope.ResourceName;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await _availability.ReportAsync(target, "state",
                "Azure environment discovery failed; metrics collection will continue.", cancellationToken);
        }

        if (state is null)
            await _availability.ReportAsync(target, "state",
                "Azure Container App state is unavailable. Verify Azure read access.", cancellationToken);
        else
            _availability.Clear(target, "state");
        if (target.ExpectedRevision is null)
        {
            await _availability.ReportAsync(target, "revision",
                "Runtime collection needs a verified deployment revision.",
                cancellationToken);
            return;
        }

        if (state != null && state.LatestRevision == target.ExpectedRevision &&
            (state.LatestRevision != target.LastRevision || state.Fqdn != target.LastFqdn))
        {
            target.LastRevision = state.LatestRevision;
            target.LastFqdn = state.Fqdn;
            if (Uri.CheckHostName(state.Fqdn) == UriHostNameType.Dns)
            {
                await using var metadata = scopeFactory.CreateAsyncScope();
                var db = metadata.ServiceProvider.GetRequiredService<AutoMateDbContext>();
                var deployment = await db.Deployments.SingleOrDefaultAsync(d => d.Id == target.DeploymentId &&
                    d.CsProject!.AppId == target.ProjectId, cancellationToken);
                if (deployment is not null)
                {
                    deployment.CloudAppUrl = "https://" + state.Fqdn;
                    if (deployment.ConfigurationSnapshotJson is { } json && !string.IsNullOrWhiteSpace(state.Image))
                    {
                        var snapshot = JsonSerializer.Deserialize<DeploymentConfigurationSnapshot>(json)!;
                        deployment.ConfigurationSnapshotJson = JsonSerializer.Serialize(snapshot with
                        {
                            Image = metadata.ServiceProvider.GetRequiredService<IDiagnosticRedactor>()
                                .RedactText(state.Image, 512)
                        });
                    }

                    await db.SaveChangesAsync(cancellationToken);
                }
            }

            await diagnostics.PublishAsync(new DeploymentDiagnosticEvent(target.ProjectId, target.DeploymentId,
                DeploymentDiagnosticSource.AzureContainerApps, DeploymentDiagnosticKind.Lifecycle,
                DeploymentDiagnosticSeverity.Information, DateTimeOffset.UtcNow, CreateAvailabilityMessage(state),
                new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, CloudWebContainerName),
                new Dictionary<string, string> { ["revision"] = state.LatestRevision },
                SourceIdentity: new DeploymentDiagnosticSourceIdentity(DeploymentDiagnosticComponent.Revision,
                    DeploymentDiagnosticStream.System, state.LatestRevision)), cancellationToken);
        }

        if (!target.MetricsDue) return;
        var metrics = await DeploymentTracing.RunAsync(DeploymentOperation.AzureMetrics, target.ProjectId,
            target.DeploymentId,
            cancellationToken, () => _containerAppClient.GetMetricsAsync(target.ResourceId,
                target.AzureCredentials.AccessToken,
                cancellationToken, target.ExpectedRevision));
        if (metrics is null)
        {
            await _availability.ReportAsync(target, "metrics",
                "Azure Container Apps metrics are unavailable. Verify Azure Monitor read access.", cancellationToken);
        }
        else
        {
            _availability.Clear(target, "metrics");
            foreach (var observation in metrics.Observations ?? [])
            {
                await diagnostics.PublishAsync(new DeploymentDiagnosticEvent(target.ProjectId, target.DeploymentId,
                        DeploymentDiagnosticSource.AzureContainerApps, DeploymentDiagnosticKind.Metric,
                        DeploymentDiagnosticSeverity.Information, observation.Timestamp,
                        $"Azure Container Apps metrics: CPU {metrics.Cpu}, memory {metrics.Memory}.",
                        new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Metrics, CloudWebContainerName),
                        new Dictionary<string, string> { ["cpu"] = metrics.Cpu, ["memory"] = metrics.Memory },
                        SourceIdentity: new DeploymentDiagnosticSourceIdentity(DeploymentDiagnosticComponent.Container,
                            DeploymentDiagnosticStream.Metric, target.ExpectedRevision),
                        Metrics: observation.Samples,
                        EventId: AzureRuntimeDiagnosticPolicy.MetricEventId(target, CloudWebContainerName,
                            observation)),
                    cancellationToken);
                await PublishMetricSummaryAsync(target, CloudWebContainerName, observation, cancellationToken);
            }
        }
    }


    /// <summary>
    ///     Delegates each database collection pass without sharing a checkpoint context or owning detached work.
    /// </summary>
    /// <param name="target">The verified active deployment and its tagged database inventory.</param>
    /// <param name="logsToken">The logs credential, or null for the metrics/discovery pass.</param>
    /// <param name="cancellationToken">The existing supervised polling token.</param>
    /// <returns>The complete database collection pass; failures retain existing source isolation.</returns>
    private Task PollDatabasesAsync(ContainerAppStreamTarget target, string? logsToken,
        CancellationToken cancellationToken)
    {
        return new AzureDatabaseDiagnostics(_databaseClient, _monitorLogsClient,
            diagnostics, scopeFactory, options, _availability).PollAsync(target, logsToken, cancellationToken);
    }

    /// <summary>
    ///     Prints web observations once per provider minute; database metrics use numeric delivery
    ///     only.
    /// </summary>
    /// <param name="target">Verified project, deployment and provider resource inventory.</param>
    /// <param name="resource">Verified provider resource identity.</param>
    /// <param name="observation">
    ///     Typed Azure resource observation preserving timestamp, units and aggregations.
    /// </param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private async Task PublishMetricSummaryAsync(ContainerAppStreamTarget target, string resource,
        AzureMetricObservation observation, CancellationToken cancellationToken)
    {
        if (target.LastMetricSummary.TryGetValue(resource, out var last) && observation.Timestamp <= last) return;
        var values = observation.Samples.Select(sample => sample.Name switch
        {
            "automate_cpu_usage_cores" => string.Create(CultureInfo.InvariantCulture,
                $"CPU {sample.Value:0.####} cores"),
            "automate_cpu_usage_percent" => string.Create(CultureInfo.InvariantCulture, $"CPU {sample.Value:0.##}%"),
            "automate_memory_used_bytes" => string.Create(CultureInfo.InvariantCulture,
                $"memory {sample.Value / 1048576:0.##} MiB"),
            "automate_memory_used_percent" => string.Create(CultureInfo.InvariantCulture,
                $"memory {sample.Value:0.##}%"),
            "automate_database_ru_usage_percent" => string.Create(CultureInfo.InvariantCulture,
                $"RU usage {sample.Value:0.##}%"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{sample.Name} {sample.Value:0.####} {sample.Unit}")
        });
        await diagnostics.PublishAsync(new DeploymentDiagnosticEvent(target.ProjectId, target.DeploymentId,
                DeploymentDiagnosticSource.AzureContainerApps, DeploymentDiagnosticKind.Lifecycle,
                DeploymentDiagnosticSeverity.Information, observation.Timestamp,
                $"{string.Join(", ", values)} (Azure Monitor, 1-minute sample).",
                new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Container, CloudWebContainerName),
                SourceIdentity: new DeploymentDiagnosticSourceIdentity(DeploymentDiagnosticComponent.Container,
                    DeploymentDiagnosticStream.Metric, resource),
                EventId: new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"{target.DeploymentId:N}/metric-summary/{resource}/{observation.Timestamp:O}"))[..16])),
            cancellationToken);
        target.LastMetricSummary[resource] = observation.Timestamp;
    }


    /// <summary>
    ///     Queries a bounded verified Azure source and advances its cursor only after durable
    ///     acknowledgement.
    /// </summary>
    /// <param name="target">Verified project, deployment and provider resource inventory.</param>
    /// <param name="source">Verified source classification, resource identity or owned input stream.</param>
    /// <param name="accessToken">The provider access token; never include it in diagnostics.</param>
    /// <param name="checkpoints">Persists source cursors only after durable acknowledgement.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private async Task TailSourceAsync(ContainerAppStreamTarget target, AzureContainerAppLogSource source,
        string accessToken, AzureContainerAppLogCheckpointStore checkpoints, CancellationToken cancellationToken)
    {
        var sourceName = source.ToString().ToLowerInvariant();
        var checkpoint = await checkpoints.GetOrCreateAsync(target.DeploymentId, sourceName, cancellationToken);
        var settings = options.Value;
        var from = (checkpoint.LastIngestionTimestamp ??
                    checkpoint.LastTimestamp ?? DateTimeOffset.UtcNow.Subtract(settings.InitialLookback))
            .Subtract(settings.OverlapWindow);
        for (var page = 0; page < Math.Clamp(settings.MaximumPagesPerPoll, 1, 100); page++)
        {
            if (string.IsNullOrWhiteSpace(target.ExpectedRevision)) return;
            if (string.IsNullOrWhiteSpace(target.LogResourceId))
            {
                await _availability.ReportAsync(target, sourceName,
                    "Azure log environment is unavailable. Verify Container App read access.", cancellationToken);
                return;
            }

            var result = await DeploymentTracing.RunAsync(DeploymentOperation.AzureLogs, target.ProjectId,
                target.DeploymentId,
                cancellationToken, () => _monitorLogsClient.QueryAsync(target.LogResourceId ?? target.ResourceId,
                    accessToken, source,
                    target.ContainerAppName, from, settings.BatchSize, cancellationToken, target.ExpectedRevision,
                    checkpoint.LastIngestionTimestamp, checkpoint.LastIngestionTieBreaker,
                    target.LogEnvironmentName, target.LogEnvironmentResourceName,
                    source == AzureContainerAppLogSource.System && target.CollectDatabases),
                result => result.IsSuccess);
            if (!result.IsSuccess)
            {
                await _availability.ReportAsync(target, sourceName, result.FailureReason!, cancellationToken);
                return;
            }

            _availability.Clear(target, sourceName);
            foreach (var record in result.Records.OrderBy(item => item.IngestionTimestampUtc ?? item.TimestampUtc)
                         .ThenBy(AzureRuntimeDiagnosticPolicy.CreateTieBreaker,
                             StringComparer.Ordinal))
            {
                var tieBreaker = AzureRuntimeDiagnosticPolicy.CreateTieBreaker(record);
                if (!AzureRuntimeDiagnosticPolicy.IsAfterCheckpoint(record, tieBreaker, checkpoint))
                {
                    AutoMateTelemetry.DiagnosticDuplicates.Add(1,
                        new KeyValuePair<string, object?>("deployment.source", "AzureContainerApps"));
                    continue;
                }

                if (record.RevisionName != target.ExpectedRevision &&
                    !(source == AzureContainerAppLogSource.System && target.CollectDatabases &&
                      record.RevisionName.Length == 0)) continue;
                var eventId =
                    new Guid(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes($"{target.DeploymentId:N}/{source}/{tieBreaker}"))[..16]);
                var diagnostic = AzureRuntimeDiagnosticPolicy.CreateLogEvent(target, source, record) with
                {
                    EventId = eventId
                };
                if (!(source == AzureContainerAppLogSource.Console &&
                      _liveLogs.WasConfirmedLive(target.DeploymentId, record)) &&
                    (diagnostics is not IDurableDeploymentDiagnosticPublisher durable ||
                     !await durable.PublishDurablyAsync(diagnostic, cancellationToken)))
                {
                    await checkpoints.SaveAsync(cancellationToken);
                    return;
                }

                checkpoint.LastTimestamp = record.TimestampUtc;
                checkpoint.LastTieBreaker = tieBreaker;
                checkpoint.LastIngestionTimestamp = record.IngestionTimestampUtc ?? record.TimestampUtc;
                checkpoint.LastIngestionTieBreaker = tieBreaker;

                var ingestionDelay = (record.IngestionTimestampUtc ?? record.TimestampUtc) - record.TimestampUtc;
                if (ingestionDelay > settings.FreshnessWarningAge)
                    await _availability.ReportAsync(target, $"{sourceName}-freshness",
                        $"Azure {sourceName} log ingestion was delayed by {ingestionDelay.TotalMinutes:0} minute(s).",
                        cancellationToken);
                else
                    _availability.Clear(target, $"{sourceName}-freshness");
            }

            checkpoint.LastSuccessfulQueryAt = DateTimeOffset.UtcNow;
            await checkpoints.SaveAsync(cancellationToken);
            if (result.Records.Count < settings.BatchSize) return;
        }

        await _availability.ReportAsync(target, sourceName + "-backlog",
            "Runtime log collection is catching up with a backlog.", cancellationToken);
    }


    /// <summary>
    ///     Validates deployment resource identity and creates a non-secret collector target.
    /// </summary>
    /// <param name="request">
    ///     Validated operation request containing the selected resource scope and metadata.
    /// </param>
    /// <param name="target">Verified project, deployment and provider resource inventory.</param>
    /// <returns>True when validation or acquisition succeeds; otherwise false.</returns>
    private static bool TryCreateStreamTarget(AzureContainerAppRuntimeStreamRequest request,
        out ContainerAppStreamTarget target)
    {
        target = null!;
        if (request.ProjectId == Guid.Empty || request.DeploymentId == Guid.Empty || request.UserId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.AzureCredentials.AccessToken) ||
            string.IsNullOrWhiteSpace(request.AzureCredentials.SubscriptionId) ||
            string.IsNullOrWhiteSpace(request.Config.CloudResourceGroupName) ||
            string.IsNullOrWhiteSpace(request.Config.CloudContainerAppName)) return false;

        var resourceId =
            $"/subscriptions/{Uri.EscapeDataString(request.AzureCredentials.SubscriptionId)}/resourceGroups/{Uri.EscapeDataString(request.Config.CloudResourceGroupName)}/providers/Microsoft.App/containerApps/{Uri.EscapeDataString(request.Config.CloudContainerAppName)}";
        target = new ContainerAppStreamTarget(request.ProjectId, request.DeploymentId, request.UserId, resourceId,
            request.Config.CloudContainerAppName, request.AzureCredentials)
        {
            ExpectedRevision = request.ExpectedRevision,
            DatabaseContainers = request.Config.Databases.Select(d => d.ContainerNameSuffix)
                .Where(n => !string.IsNullOrWhiteSpace(n)).ToHashSet(StringComparer.Ordinal)
        };
        return true;
    }

    /// <summary>
    ///     Formats observed Container App availability without claiming application health.
    /// </summary>
    /// <param name="state">The project queue-state snapshot or fixed attention state.</param>
    /// <returns>The availability message derived from the validated input.</returns>
    private static string CreateAvailabilityMessage(AzureContainerAppState state)
    {
        return
            $"Azure Container App is available{(string.IsNullOrWhiteSpace(state.Fqdn) ? string.Empty : $" at https://{state.Fqdn}")}. Latest ready revision: {state.LatestRevision}.";
    }

    /// <summary>
    ///     Restores collection for eligible verified cloud deployments after host restart.
    /// </summary>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private async Task RecoverTargetsAsync(CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
        var credentials = scope.ServiceProvider.GetService<AzureArmCredentialsProvider>();
        if (credentials is null) return;
        var deployments = await db.Deployments.AsNoTracking().Where(d => d.Status == DeploymentStatus.Running &&
                                                                         d.CloudResourceId != null &&
                                                                         d.CloudContainerAppName != null &&
                                                                         d.CloudContainerRevision != null)
            .Select(d => new
            {
                d.Id,
                d.CsProject!.AppId,
                d.CsProject!.Application.UserId,
                d.CloudResourceId,
                d.CloudContainerAppName,
                d.CloudContainerRevision
            }).ToListAsync(token);
        foreach (var d in deployments)
        {
            if (_targets.ContainsKey(d.Id)) continue;
            try
            {
                var azure = await credentials.GetAsync(d.UserId, token);
                _targets.TryAdd(d.Id, new ContainerAppStreamTarget(d.AppId, d.Id, d.UserId, d.CloudResourceId!,
                        d.CloudContainerAppName!, azure)
                    { ExpectedRevision = d.CloudContainerRevision });
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                logger.LogWarning("Cloud runtime recovery requires Azure reconnection for deployment {DeploymentId}.",
                    d.Id);
            }
        }
    }
}
