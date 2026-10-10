using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Abstractions.Diagnostics;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

/// <summary>Imports bounded pages of still-retained diagnostics, with crash-safe per-deployment progress.</summary>
public sealed class DeploymentArchiveBackfillWorker(
    IServiceScopeFactory scopes,
    IDeploymentArchive archive,
    IOptions<DiskSpoolOptions> spool,
    IOptions<TelemetryStorageOptions> settings,
    ILogger<DeploymentArchiveBackfillWorker> logger,
    DiskTelemetrySpool? pending = null) : BackgroundService
{
    /// <summary>Discovery position; a restart safely revisits completed durable checkpoints.</summary>
    private int _offset;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                await ImportBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Archive backfill unavailable: {FailureType}.", ex.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Bounds each discovery pass and each backend query independently of deployment age.</summary>
    public async Task ImportBatchAsync(CancellationToken token)
    {
        if (settings.Value.ManagedService && !settings.Value.ManagedDataProcessingApproved)
            throw new InvalidOperationException("Managed telemetry processing is not approved.");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
        var logs = scope.ServiceProvider.GetRequiredService<IDeploymentLogQuery>();
        var metrics = scope.ServiceProvider.GetRequiredService<IDeploymentMetricQuery>();
        var projects = await db.Deployments.AsNoTracking()
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).Skip(_offset).Take(20)
            .Select(d => new
                { d.Id, d.CreatedAt, Project = d.CsProject!.AppId, Tenant = d.CsProject.Application.UserId })
            .ToArrayAsync(token);
        _offset = projects.Length < 20 ? 0 : _offset + projects.Length;
        foreach (var deployment in projects)
            try
            {
                var directory = Path.Combine(spool.Value.Directory, "archive-backfill", deployment.Tenant.ToString("N"),
                    deployment.Project.ToString("N"));
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, deployment.Id.ToString("N") + ".checkpoint");
                var state = File.Exists(path)
                    ? await ReadAsync(path, token)
                    : new Progress(DateTimeOffset.UtcNow, 0, 0,
                        deployment.CreatedAt > DateTimeOffset.UtcNow.AddDays(-30)
                            ? deployment.CreatedAt
                            : DateTimeOffset.UtcNow.AddDays(-30),
                        false, false);
                if (state.Complete) continue;
                if (pending is not null)
                {
                    var buffered =
                        await pending.PendingAsync(deployment.Tenant, deployment.Project, deployment.Id, token);
                    foreach (var entry in buffered.Events) await archive.AppendAsync(entry, token);
                }

                // Use the fixed cutoff for this import so later live archive samples are not imported again as aggregates.
                if (!state.LegacyDone)
                {
                    var next = await db.Deployments.Where(d =>
                            d.CsProject!.AppId == deployment.Project && d.CreatedAt > deployment.CreatedAt)
                        .OrderBy(d => d.CreatedAt).Select(d => (DateTimeOffset?)d.CreatedAt).FirstOrDefaultAsync(token);
                    var now = DateTimeOffset.UtcNow;
                    var rows = await db.DeploymentDiagnosticRecords.AsNoTracking().Where(r =>
                            r.ProjectId == deployment.Project &&
                            r.ExpiresAt > now && r.OrderId > state.LegacyCursor && r.TimestampUtc < state.Cutoff &&
                            (r.DeploymentId == deployment.Id || (r.DeploymentId == null && r.DeliveryJson == null &&
                                                                 (r.Source == "GitHubActions" ||
                                                                  r.Source == "DockerCompose") &&
                                                                 r.TimestampUtc >= deployment.CreatedAt &&
                                                                 (next == null || r.TimestampUtc < next))))
                        .OrderBy(r => r.OrderId).Take(500).ToArrayAsync(token);
                    foreach (var row in rows)
                        await archive.AppendAsync(Envelope(row, deployment.Tenant, deployment.Id), token);
                    state = state with
                    {
                        LegacyCursor = rows.LastOrDefault()?.OrderId ?? state.LegacyCursor,
                        LegacyDone = rows.Length < 500
                    };
                    await WriteAsync(path, state, token);
                }

                if (!state.LogsDone)
                {
                    var rows = await logs.ReadAsync(deployment.Tenant, deployment.Project, deployment.Id,
                        state.LogCursor, false, 500, token, deployment.CreatedAt);
                    // Live events at/after cutoff already require archive persistence before acknowledgment.
                    foreach (var row in rows.Where(r => r.Event.TimestampUtc < state.Cutoff))
                        await archive.AppendAsync(row, token);
                    state = state with
                    {
                        LogCursor = rows.LastOrDefault()?.OrderId ?? state.LogCursor,
                        LogsDone = rows.Count < 500 || rows.Any(r => r.Event.TimestampUtc >= state.Cutoff)
                    };
                    await WriteAsync(path, state, token);
                }

                if (state.MetricStart < state.Cutoff)
                {
                    var end = state.MetricStart.AddDays(1) < state.Cutoff ? state.MetricStart.AddDays(1) : state.Cutoff;
                    if (end > DateTimeOffset.UtcNow.AddDays(-30))
                    {
                        var points = await metrics.ReadAsync(deployment.Tenant, deployment.Project, deployment.Id,
                            state.MetricStart, end, 1000, token);
                        foreach (var batch in points.Where(p => p.Timestamp < state.Cutoff).Chunk(1000))
                            await archive.ImportMetricsAsync(new ArchiveMetricImport(deployment.Tenant,
                                deployment.Project,
                                deployment.Id, batch), token);
                    }

                    state = state with { MetricStart = end };
                    await WriteAsync(path, state, token);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Archive backfill deployment unavailable: {FailureType}.", ex.GetType().Name);
            }
    }

    /// <summary>Preserves legacy event identity, order, channel and trace correlation.</summary>
    private static DeploymentLogEnvelope Envelope(DeploymentDiagnosticRecord row, Guid tenant, Guid deployment)
    {
        if (row.DeliveryJson is not null && row.TenantId is not null && row.StoredAt is not null)
            return DeploymentTelemetryStore.Envelope(row);
        var source = Enum.TryParse<DeploymentDiagnosticSource>(row.Source, out var s)
            ? s
            : DeploymentDiagnosticSource.AutoMate;
        var kind = Enum.TryParse<DeploymentDiagnosticKind>(row.Kind, out var k) ? k : DeploymentDiagnosticKind.Log;
        var severity = Enum.TryParse<DeploymentDiagnosticSeverity>(row.Severity, out var level)
            ? level
            : DeploymentDiagnosticSeverity.Information;
        var e = new DeploymentDiagnosticEvent(row.ProjectId, deployment, source, kind, severity, row.TimestampUtc,
            row.Message, new DeploymentTerminalChannel(kind == DeploymentDiagnosticKind.Metric
                ? DeploymentTerminalChannelKind.Metrics
                : DeploymentTerminalChannelKind.Build, row.Cursor),
            TraceId: row.TraceId, SpanId: row.SpanId, Sequence: row.Sequence,
            SourceIdentity: row.SourceIdentityJson is null
                ? null
                : JsonSerializer.Deserialize<DeploymentDiagnosticSourceIdentity>(row.SourceIdentityJson),
            Metrics: row.MetricSamplesJson is null
                ? null
                : JsonSerializer.Deserialize<DeploymentMetricSample[]>(row.MetricSamplesJson),
            EventId: row.Id);
        return new DeploymentLogEnvelope(row.Id, tenant, row.OrderId, row.StoredAt ?? row.TimestampUtc, row.ExpiresAt,
            e,
            row.TerminalChannel ?? (row.Source == "GitHubActions" ? "github-actions" : "build"));
    }

    /// <summary>Rejects damaged checkpoints rather than silently skipping history.</summary>
    private static async Task<Progress> ReadAsync(string path, CancellationToken token)
    {
        var record = JsonSerializer.Deserialize<Checkpoint>(await File.ReadAllTextAsync(path, token))!;
        if (record.Checksum != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record.Payload))))
            throw new InvalidDataException("Archive checkpoint checksum mismatch.");
        return JsonSerializer.Deserialize<Progress>(record.Payload)!;
    }

    /// <summary>Only advances after all corresponding archive writes have been durably flushed.</summary>
    private static async Task WriteAsync(string path, Progress state, CancellationToken token)
    {
        var payload = JsonSerializer.Serialize(state);
        var record = new Checkpoint(payload, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));
        await using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None,
                         4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, record, cancellationToken: token);
            await stream.FlushAsync(token);
            stream.Flush(true);
        }

        File.Move(path + ".tmp", path, true);
    }

    /// <summary>Only owner/deployment discovery metadata and bounded cursors are kept here.</summary>
    private sealed record Progress(
        DateTimeOffset Cutoff,
        long LegacyCursor,
        long LogCursor,
        DateTimeOffset MetricStart,
        bool LegacyDone,
        bool LogsDone)
    {
        /// <summary>All retained sources have reached their fixed import boundary.</summary>
        public bool Complete => LegacyDone && LogsDone && MetricStart >= Cutoff;
    }

    /// <summary>Checksummed crash-recovery metadata.</summary>
    private sealed record Checkpoint(string Payload, string Checksum);
}