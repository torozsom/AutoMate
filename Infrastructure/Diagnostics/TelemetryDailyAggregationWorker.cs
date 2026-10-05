using Application.Abstractions.Diagnostics;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Diagnostics;

/// <summary>Runs on the single telemetry service, not every web replica; retries replace daily results.</summary>
public sealed class TelemetryDailyAggregationWorker(
    IServiceScopeFactory scopes,
    IOptions<TelemetryStorageOptions> options,
    ILogger<TelemetryDailyAggregationWorker> logger,
    IDiagnosticRedactor redactor) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await AggregateOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Daily telemetry aggregation delayed: {FailureType}.", ex.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Replaces recent daily observations and deletes expired summaries without zero-filling missing metrics.</summary>
    public async Task AggregateOnceAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.DeploymentDailyTelemetry.Where(d => d.DayUtc < now.AddDays(-365)).ExecuteDeleteAsync(token);
        var deployments = await db.Deployments.AsNoTracking().Where(d =>
                (!options.Value.ManagedService || d.CsProject!.Application.ManagedTelemetryConsent) &&
                (d.Status == DeploymentStatus.Running || d.UpdatedAt >= now.AddDays(-2)))
            .Select(d => new { d.Id, d.CreatedAt, d.CsProject!.AppId, d.CsProject.Application.UserId })
            .ToListAsync(token);
        foreach (var deployment in deployments)
            for (var offset = -2; offset <= 0; offset++)
            {
                var day = new DateTimeOffset(now.UtcDateTime.Date.AddDays(offset), TimeSpan.Zero);
                var end = day.AddDays(1) < now ? day.AddDays(1) : now;
                if (end <= deployment.CreatedAt) continue;
                var start = deployment.CreatedAt > day ? deployment.CreatedAt : day;
                try
                {
                    var metrics = await scope.ServiceProvider.GetRequiredService<IDailyDeploymentMetricQuery>()
                        .ReadDailyAsync(deployment.UserId, deployment.AppId, deployment.Id, start, end, token);
                    metrics = metrics.Select(metric => metric with
                    {
                        Container = redactor.RedactText(metric.Container, 128),
                        Name = redactor.RedactText(metric.Name, 128),
                        Unit = redactor.RedactText(metric.Unit, 64)
                    }).ToArray();
                    var errors = await scope.ServiceProvider.GetRequiredService<IDeploymentErrorCountQuery>()
                        .CountErrorsAsync(deployment.UserId, deployment.AppId, deployment.Id, start, end, token);
                    var rows = await db.DeploymentDailyTelemetry
                        .Where(d => d.DeploymentId == deployment.Id && d.DayUtc == day).ToListAsync(token);
                    // Preserve previously observed values, but disclose a later missing series rather than claiming fresh completeness.
                    foreach (var row in rows.Where(row => row.Metric != "observed_log_errors" &&
                                                          !metrics.Any(metric =>
                                                              metric.Name == row.Metric &&
                                                              metric.Container == row.Container)))
                        row.Incomplete = true;
                    foreach (var metric in metrics)
                    {
                        var row = rows.SingleOrDefault(d => d.Metric == metric.Name && d.Container == metric.Container);
                        if (row is null)
                        {
                            row = new DeploymentDailyTelemetry
                            {
                                UserId = deployment.UserId,
                                ProjectId = deployment.AppId,
                                DeploymentId = deployment.Id,
                                DayUtc = day,
                                Container = metric.Container,
                                Metric = metric.Name,
                                Unit = metric.Unit
                            };
                            db.DeploymentDailyTelemetry.Add(row);
                        }

                        row.SampleCount = metric.SampleCount;
                        row.Sum = metric.Sum;
                        row.Minimum = metric.Minimum;
                        row.Maximum = metric.Maximum;
                        row.Incomplete = metric.SampleCount <
                                         (end - start).TotalSeconds / options.Value.RuntimeSampleSeconds;
                    }

                    var errorRow = rows.SingleOrDefault(d => d.Metric == "observed_log_errors");
                    if (errorRow is null)
                    {
                        errorRow = new DeploymentDailyTelemetry
                        {
                            UserId = deployment.UserId,
                            ProjectId = deployment.AppId,
                            DeploymentId = deployment.Id,
                            DayUtc = day,
                            Container = "all",
                            Metric = "observed_log_errors",
                            Unit = "entries"
                        };
                        db.DeploymentDailyTelemetry.Add(errorRow);
                    }

                    errorRow.ObservedErrors = errors;
                    // At-least-once delivery and collection gaps prevent claiming an exact application error total.
                    errorRow.Incomplete = true;
                    await db.SaveChangesAsync(token);
                    db.ChangeTracker.Clear();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    db.ChangeTracker.Clear();
                    logger.LogWarning("Daily telemetry unavailable for deployment {DeploymentId}: {FailureType}.",
                        deployment.Id, ex.GetType().Name);
                }
            }
    }
}