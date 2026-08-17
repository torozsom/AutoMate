using System.Collections.Concurrent;
using Core.DTO;
using Core.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Services.Docker;

namespace Services.Orchestration;

/// <summary>
///     Manages background Docker log and metrics streaming for local deployments.
/// </summary>
internal sealed class LocalDeploymentLogStreamManager(
    IServiceScopeFactory serviceScopeFactory,
    ILogger logger)
{
    /// <summary>
    ///     Active per-project cancellation sources for background log streaming workers.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> ActiveLogStreams = new();

    /// <summary>
    ///     Starts web and database container log/metric streams for a running deployment.
    /// </summary>
    public void Start(DeploymentConfigDto config, CsProject csProject)
    {
        var cts = new CancellationTokenSource();
        ActiveLogStreams.AddOrUpdate(config.ProjectId, cts, (_, oldCts) =>
        {
            oldCts.Cancel();
            return cts;
        });

        var token = cts.Token;

        _ = Task.Run(async () => await RunStreamsAsync(config, csProject, cts, token), token);
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
    private async Task RunStreamsAsync(DeploymentConfigDto config, CsProject csProject, CancellationTokenSource cts,
        CancellationToken token)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var scopedDockerService = scope.ServiceProvider.GetRequiredService<IDockerService>();
        var streamingTasks = CreateStreamingTasks(scopedDockerService, config, csProject, token);

        try
        {
            await Task.WhenAll(streamingTasks);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogInformation("[LocalDeploymentOrchestrator] Log streaming cancelled for Project ID {Id}." +
                                  "Exception: {Ex}", config.ProjectId, ex);
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
        CsProject csProject, CancellationToken token)
    {
        var appName = OrchestrationNameNormalizer.NormalizeContainerName(config.ProjectName);
        var webContainerName = $"{OrchestrationNameNormalizer.NormalizeContainerName(csProject.Name)}-web";
        var streamingTasks = new List<Task>
        {
            dockerService.StreamContainerLogsAsync(webContainerName, config.ProjectId, "web", token),
            dockerService.StreamContainerMetricsAsync(webContainerName, config.ProjectId, "web", token)
        };

        if (config.Databases == null)
            return streamingTasks;

        foreach (var database in config.Databases)
        {
            var dbContainerName = $"{appName}-{database.ContainerNameSuffix}";
            streamingTasks.Add(dockerService.StreamContainerLogsAsync(
                dbContainerName,
                config.ProjectId,
                database.ContainerNameSuffix,
                token));

            streamingTasks.Add(dockerService.StreamContainerMetricsAsync(
                dbContainerName,
                config.ProjectId,
                database.ContainerNameSuffix,
                token));
        }

        return streamingTasks;
    }
}