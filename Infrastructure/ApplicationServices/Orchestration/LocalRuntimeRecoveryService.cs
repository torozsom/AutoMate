using Application.Abstractions.Docker;
using Application.Abstractions.Scanning;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Application.Orchestration;

/// <summary>Restores supervisors for current local deployments; supervisors enforce runtime collection interest.</summary>
public sealed class LocalRuntimeRecoveryService(
    IServiceScopeFactory scopes,
    ILocalDeploymentDiagnostics diagnostics,
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
                logger.LogWarning("Local runtime collection recovery will retry: {FailureType}.",
                    exception.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Registers only missing supervisors for current local deployments, without starting idle runtime sources.</summary>
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
        for (var offset = 0;; offset += 100)
        {
            var deployments = await query.Skip(offset).Take(100).ToListAsync(token);
            foreach (var deployment in deployments)
            {
                var project = deployment.CsProject!;
                var app = project.Application!;
                if (diagnostics.IsActive(app.Id, deployment.Id)) continue;
                try
                {
                    var config = await scope.ServiceProvider.GetRequiredService<IProjectScannerService>()
                        .AnalyzeDependenciesAsync(app, project, token);
                    await diagnostics.RegisterAsync(LocalDockerTargets.Create(config, project.Name, deployment.Id),
                        false, token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning("Local runtime collection recovery unavailable: {FailureType}.",
                        exception.GetType().Name);
                }
            }

            if (deployments.Count < 100) break;
        }
    }
}