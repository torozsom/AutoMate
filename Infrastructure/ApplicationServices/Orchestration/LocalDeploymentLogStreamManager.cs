using System.Collections.Concurrent;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.Docker;
using Domain.DTO;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Application.Orchestration;

/// <summary>
///     Manages background Docker log and metrics streaming for local deployments.
/// </summary>
internal sealed class LocalDeploymentLogStreamManager(
    IServiceScopeFactory serviceScopeFactory,
    ILogger logger,
    CancellationToken applicationStopping = default)
{
    /// <summary>
    ///     Active per-project cancellation sources for background log streaming workers.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> ActiveLogStreams = new();

    /// <summary>Checks whether the host already supervises a project.</summary>
    public static bool IsActive(Guid projectId)
    {
        return ActiveLogStreams.ContainsKey(projectId);
    }

    /// <summary>Restores a collector without replacing an existing deployment's supervisor.</summary>
    public void EnsureStarted(DeploymentConfigDto config, CsProject csProject, Guid deploymentId)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
        if (!ActiveLogStreams.TryAdd(config.ProjectId, cts))
        {
            cts.Dispose();
            return;
        }

        _ = Task.Run(() => RunStreamsAsync(config, csProject, deploymentId, cts, cts.Token));
    }

    /// <summary>
    ///     Starts web and database container log/metric streams for a running deployment.
    /// </summary>
    public void Start(DeploymentConfigDto config, CsProject csProject, Guid deploymentId)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
        ActiveLogStreams.AddOrUpdate(config.ProjectId, cts, (_, oldCts) =>
        {
            oldCts.Cancel();
            return cts;
        });

        var token = cts.Token;

        _ = Task.Run(async () => await RunStreamsAsync(config, csProject, deploymentId, cts, token), token);
    }

    /// <summary>
    ///     Cancels and disposes active streams for a project if any are registered.
    /// </summary>
    public async Task StopAsync(Guid projectId)
    {
        if (!ActiveLogStreams.TryRemove(projectId, out var cts))
            return;

        logger.LogInformation(
            "[LocalDeploymentOrchestrator] Cancelling active log streams for Project ID {Id}...", projectId);
        await cts.CancelAsync();
        cts.Dispose();
    }

    /// <summary>
    ///     Runs all configured stream tasks inside an independent service scope.
    /// </summary>
    private async Task RunStreamsAsync(DeploymentConfigDto config, CsProject csProject, Guid deploymentId,
        CancellationTokenSource cts,
        CancellationToken token)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var scopedDockerService = scope.ServiceProvider.GetRequiredService<IDockerService>();
        try
        {
            // Host-owned collection follows consent independently of page views.
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            CancellationTokenSource? collection = null;
            Task? running = null;
            try
            {
                do
                {
                    await using var policyScope = serviceScopeFactory.CreateAsyncScope();
                    var db = policyScope.ServiceProvider.GetRequiredService<AutoMateDbContext>();
                    var current = await db.Deployments.Where(d => d.CsProject!.AppId == config.ProjectId)
                        .OrderByDescending(d => d.CreatedAt).Select(d => new { d.Id, d.Status })
                        .FirstOrDefaultAsync(token);
                    if (current?.Id != deploymentId || current.Status != DeploymentStatus.Running) break;
                    var enabled = await db.Applications
                        .AnyAsync(p => p.Id == config.ProjectId && p.RuntimeDiagnosticsEnabled, token);
                    enabled |= policyScope.ServiceProvider.GetRequiredService<IDeploymentRuntimeViewers>()
                        .HasViewers(config.ProjectId, deploymentId);
                    if (enabled && running is null)
                    {
                        collection = CancellationTokenSource.CreateLinkedTokenSource(token);
                        running = Task.WhenAll(CreateStreamingTasks(scopedDockerService, config, csProject,
                            deploymentId, collection.Token));
                    }

                    if ((!enabled || running?.IsCompleted == true) && running is not null)
                    {
                        await collection!.CancelAsync();
                        try
                        {
                            await running;
                        }
                        catch (OperationCanceledException)
                        {
                        }

                        collection.Dispose();
                        collection = null;
                        running = null;
                    }
                } while (await timer.WaitForNextTickAsync(token));
            }
            finally
            {
                if (collection is not null)
                {
                    await collection.CancelAsync();
                    try
                    {
                        if (running is not null) await running;
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    collection.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Log streaming cancelled for project {ProjectId}.", config.ProjectId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LocalDeploymentOrchestrator] Error streaming logs for Project ID {Id}.",
                config.ProjectId);
        }
        finally
        {
            if (ActiveLogStreams.TryGetValue(config.ProjectId, out var activeCts) &&
                ReferenceEquals(activeCts, cts))
                ActiveLogStreams.TryRemove(config.ProjectId, out _);

            cts.Dispose();
        }
    }

    /// <summary>
    ///     Creates Docker log and metric stream tasks for the web container and configured databases.
    /// </summary>
    private static List<Task> CreateStreamingTasks(IDockerService dockerService, DeploymentConfigDto config,
        CsProject csProject, Guid deploymentId, CancellationToken token)
    {
        var appName = OrchestrationNameNormalizer.NormalizeContainerName(config.ProjectName);
        var webContainerName = $"{OrchestrationNameNormalizer.NormalizeContainerName(csProject.Name)}-web";
        var streamingTasks = new List<Task>
        {
            dockerService.StreamContainerLogsAsync(webContainerName, config.ProjectId, deploymentId, "web", token),
            dockerService.StreamContainerMetricsAsync(webContainerName, config.ProjectId, deploymentId, "web", token)
        };

        if (config.Databases == null)
            return streamingTasks;

        foreach (var database in config.Databases)
        {
            var dbContainerName = $"{appName}-{database.ContainerNameSuffix}";
            streamingTasks.Add(dockerService.StreamContainerLogsAsync(
                dbContainerName,
                config.ProjectId,
                deploymentId,
                database.ContainerNameSuffix,
                token));

            streamingTasks.Add(dockerService.StreamContainerMetricsAsync(
                dbContainerName,
                config.ProjectId,
                deploymentId,
                database.ContainerNameSuffix,
                token));
        }

        return streamingTasks;
    }
}