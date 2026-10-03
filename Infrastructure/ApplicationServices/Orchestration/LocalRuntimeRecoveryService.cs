using Application.Abstractions.Diagnostics;
using Application.Abstractions.Scanning;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Application.Orchestration;

/// <summary>Restores opted-in or actively viewed local runtime collectors after an AutoMate restart.</summary>
public sealed class LocalRuntimeRecoveryService(
    IServiceScopeFactory scopes,
    IDeploymentRuntimeViewers viewers,
    IHostApplicationLifetime lifetime,
    ILogger<LocalRuntimeRecoveryService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                await RecoverOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Local runtime collection recovery will retry.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Finds current local deployments and starts only missing collectors with collection interest.</summary>
    internal async Task RecoverOnceAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
        var query = db.Deployments.AsNoTracking()
            .Where(d => d.Status == DeploymentStatus.Running &&
                        d.CsProject!.Application!.SourceType == SourceType.Local &&
                        !db.Deployments.Any(newer => newer.CsProject!.AppId == d.CsProject.AppId &&
                                                     newer.CreatedAt > d.CreatedAt))
            .Include(d => d.CsProject!).ThenInclude(p => p.Application)
            .OrderBy(d => d.Id);
        var manager = new LocalDeploymentLogStreamManager(scopes, logger, lifetime.ApplicationStopping);
        for (var offset = 0;; offset += 100)
        {
            var deployments = await query.Skip(offset).Take(100).ToListAsync(token);
            foreach (var deployment in deployments)
            {
                var project = deployment.CsProject!;
                var app = project.Application!;
                if (LocalDeploymentLogStreamManager.IsActive(app.Id) ||
                    (!app.RuntimeDiagnosticsEnabled && !viewers.HasViewers(app.Id, deployment.Id))) continue;
                try
                {
                    var config = await scope.ServiceProvider.GetRequiredService<IProjectScannerService>()
                        .AnalyzeDependenciesAsync(app, project, token);
                    manager.EnsureStarted(config, project, deployment.Id);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Could not restore runtime collection for project {ProjectId}.",
                        app.Id);
                }
            }

            if (deployments.Count < 100) break;
        }
    }
}