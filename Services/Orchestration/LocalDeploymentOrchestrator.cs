using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Core.DTO;
using Core.Entities;
using Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Services.Data;
using Services.Docker;
using Services.Scanner;
using Services.Templating;

namespace Services.Orchestration;

/// <summary>
///     Orchestrates the process of deploying .NET projects locally by managing interactions with
///     various services, including database, system scanning, project scanning, templating, and Docker.
/// </summary>
public partial class LocalDeploymentOrchestrator(
    AutoMateDbContext dbContext,
    ILocalSystemScannerService systemScanner,
    IProjectScannerService projectScanner,
    ITemplatingService templateService,
    IDockerService dockerService,
    ILogger<LocalDeploymentOrchestrator> logger,
    IServiceScopeFactory serviceScopeFactory,
    IDeploymentStatusNotifier statusNotifier)
    : ILocalDeploymentOrchestrator
{
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> ActiveLogStreams = new();

    /// <summary>
    ///     Deploys a local .NET project by orchestrating the entire process.
    /// </summary>
    /// <param name="config">The configuration for the deployment.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation if needed.</param>
    /// <returns>
    ///     A <see cref="Task" /> representing the asynchronous operation, with a result of type
    ///     <see cref="Deployment" />, which contains details of the deployment, such as status,
    ///     image tag, and deployment timestamp.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when the project or its configuration cannot be found for the given ID.
    /// </exception>
    /// <exception cref="Exception">
    ///     Thrown if an unexpected error occurs during the deployment process.
    /// </exception>
    public async Task<Deployment> DeployLocalProjectAsync(DeploymentConfigDto config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        logger.LogInformation(
            "[LocalDeploymentOrchestrator] Starting Deployment Process for project '{ProjectName}'...",
            config.ProjectName);

        var csProject = await dbContext.CsProjects.FirstOrDefaultAsync(csp => csp.Id == config.CsProjectId,
            cancellationToken);

        if (csProject == null)
        {
            logger.LogError(
                "[LocalDeploymentOrchestrator] Deployment failed: Database record for CsProject {Id} not found.",
                config.CsProjectId);
            throw new InvalidOperationException($"Project with ID {config.CsProjectId} not found in the database.");
        }

        var deployment = new Deployment
        {
            CsProjectId = csProject.Id,
            ImageTag = GenerateImageTag(csProject.Name, csProject.Id)
        };

        // Save the deployment to the database
        dbContext.Deployments.Add(deployment);
        await dbContext.SaveChangesAsync(cancellationToken);
        statusNotifier.NotifyStatusChanged(config.ProjectId, deployment.Status);

        try
        {
            await ExecuteDeploymentStepsAsync(config, csProject, deployment, cancellationToken);
            return deployment;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "[LocalDeploymentOrchestrator] Deployment failed during execution for project '{ProjectName}'.",
                config.ProjectName);
            await SafeUpdateDeploymentStatusAsync(config.ProjectId, deployment, DeploymentStatus.Failed,
                cancellationToken);
            throw;
        }
    }


    /// <summary>
    ///     Stops an existing deployment for a local .NET project.
    /// </summary>
    /// <param name="projectId">The unique identifier of the project.</param>
    /// <param name="projectName">The name of the project.</param>
    /// <param name="csProjectPath">The path to the main C# project file.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation if needed.</param>
    public async Task StopDeploymentAsync(Guid projectId, string projectName, string csProjectPath,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[LocalDeploymentOrchestrator] Stopping deployment for Project ID {Id}...", projectId);

        var solutionRoot = await systemScanner.FindSolutionRootAsync(csProjectPath, cancellationToken);
        var automateDir = Path.Combine(solutionRoot, ".automate");

        if (!Directory.Exists(automateDir))
        {
            logger.LogWarning(
                "[LocalDeploymentOrchestrator] No .automate directory found at {Path}. Cannot stop deployment.",
                automateDir);
            return;
        }

        var isStopped = await dockerService.RunDockerComposeDownAsync(automateDir, projectName, projectId,
            cancellationToken);

        if (isStopped)
        {
            if (ActiveLogStreams.TryRemove(projectId, out var cts))
            {
                logger.LogInformation(
                    "[LocalDeploymentOrchestrator] Cancelling active log streams for Project ID {Id}...", projectId);
                await cts.CancelAsync();
                cts.Dispose();
            }

            var latestDeployment = await dbContext.Deployments
                .Where(d => d.CsProject!.AppId == projectId)
                .OrderByDescending(d => d.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (latestDeployment != null && latestDeployment.Status != DeploymentStatus.Stopped)
            {
                await SafeUpdateDeploymentStatusAsync(projectId, latestDeployment, DeploymentStatus.Stopped,
                    cancellationToken);
                logger.LogInformation(
                    "[LocalDeploymentOrchestrator] Deployment stopped successfully for Project ID {Id}.", projectId);
            }
        }
        else
        {
            logger.LogError("[LocalDeploymentOrchestrator] Failed to stop deployment for Project ID {Id}.", projectId);
        }
    }


    /// <summary>
    ///     Executes the main steps of the deployment process, including locating the solution root,
    ///     scanning the project for dependencies, generating necessary templates, and running Docker Compose.
    /// </summary>
    /// <param name="config"></param>
    /// <param name="csProject"></param>
    /// <param name="deployment"></param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation if needed.</param>
    /// <exception cref="InvalidOperationException"></exception>
    private async Task ExecuteDeploymentStepsAsync(DeploymentConfigDto config, CsProject csProject,
        Deployment deployment, CancellationToken cancellationToken)
    {
        logger.LogInformation("[LocalDeploymentOrchestrator] Step 1/4: Locating solution root for {Path}...",
            csProject.Path);
        var solutionRoot = await systemScanner.FindSolutionRootAsync(csProject.Path, cancellationToken);

        var automateDir = Path.Combine(solutionRoot, ".automate");
        if (!Directory.Exists(automateDir))
            Directory.CreateDirectory(automateDir);

        logger.LogInformation("[LocalDeploymentOrchestrator] Step 2/4: Scanning project content for dependencies...");
        var metadata = await projectScanner.ScanProjectContentAsync(csProject.Path, cancellationToken);

        logger.LogInformation(
            "[LocalDeploymentOrchestrator] Step 3/4: Generating Infrastructure-as-Code files (Dockerfile, docker-compose)...");
        await templateService.GenerateAndSaveAllTemplatesAsync(config, metadata, csProject.Name, automateDir,
            cancellationToken);

        logger.LogInformation("[LocalDeploymentOrchestrator] Step 4/4: Starting Docker Compose deployment...");
        await SafeUpdateDeploymentStatusAsync(config.ProjectId, deployment, DeploymentStatus.Starting,
            cancellationToken);

        var isDockerSuccess =
            await dockerService.RunDockerComposeUpAsync(automateDir, config.ProjectName, config.ProjectId,
                cancellationToken);

        if (!isDockerSuccess)
            throw new InvalidOperationException("Docker Compose process returned an error or timed out. " +
                                                "Check server console for details.");

        var systemUrl = $"http://localhost:{config.ExposedPort}";
        logger.LogInformation("--- Deployment Finished Successfully! System live at {Url} ---", systemUrl);

        await SafeUpdateDeploymentStatusAsync(config.ProjectId, deployment, DeploymentStatus.Running,
            cancellationToken);

        var cts = new CancellationTokenSource();
        ActiveLogStreams.AddOrUpdate(config.ProjectId, cts, (_, oldCts) =>
        {
            oldCts.Cancel();
            return cts;
        });

        var token = cts.Token;

        // Start streaming container logs in the background using a new scope so it outlives this request scope
        _ = Task.Run(async () =>
        {
            using var scope = serviceScopeFactory.CreateScope();
            var scopedDockerService = scope.ServiceProvider.GetRequiredService<IDockerService>();

            var appName = NormalizeContainerName(config.ProjectName);
            var streamingTasks = new List<Task>();

            // Web container
            var webContainerName = $"{NormalizeContainerName(csProject.Name)}-web";

            streamingTasks.Add(scopedDockerService.StreamContainerLogsAsync(
                webContainerName,
                config.ProjectId,
                "web",
                token)
            );

            streamingTasks.Add(scopedDockerService.StreamContainerMetricsAsync(
                webContainerName,
                config.ProjectId,
                "web",
                token)
            );

            // Database containers
            if (config.Databases != null)
                foreach (var db in config.Databases)
                {
                    var dbContainerName = $"{appName}-{db.ContainerNameSuffix}";
                    streamingTasks.Add(scopedDockerService.StreamContainerLogsAsync(
                        dbContainerName,
                        config.ProjectId,
                        db.ContainerNameSuffix,
                        token)
                    );

                    streamingTasks.Add(scopedDockerService.StreamContainerMetricsAsync(
                        dbContainerName,
                        config.ProjectId,
                        db.ContainerNameSuffix,
                        token)
                    );
                }

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
        }, token);
    }


    /// <summary>
    ///     Safely updates the deployment status in the database, handling any
    ///     potential exceptions that may occur during the update process.
    /// </summary>
    /// <param name="projectId">
    ///     The unique identifier of the project.
    /// </param>
    /// <param name="deployment">
    ///     The <see cref="Deployment" /> entity whose status is to be updated.
    /// </param>
    /// <param name="status">
    ///     The new <see cref="DeploymentStatus" /> value to set for the deployment.
    /// </param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation if needed.</param>
    private async Task SafeUpdateDeploymentStatusAsync(Guid projectId, Deployment deployment, DeploymentStatus status,
        CancellationToken cancellationToken = default)
    {
        try
        {
            deployment.Status = status;
            await dbContext.SaveChangesAsync(cancellationToken);
            statusNotifier.NotifyStatusChanged(projectId, status);
        }
        catch (DbUpdateException ex)
        {
            logger.LogCritical(ex, "[LocalDeploymentOrchestrator] CRITICAL: Failed to update " +
                                   "deployment status to '{Status}' for Deployment ID {Id}.", status, deployment.Id);
        }
    }


    /// <summary>
    ///     Generates a unique and descriptive Docker image tag based on the project name and ID.
    /// </summary>
    /// <param name="projectName">
    ///     The name of the project, which will be sanitized and included in the image tag for readability.
    /// </param>
    /// <param name="projectId">
    ///     The unique identifier of the project, which will be included in the image tag
    ///     to ensure uniqueness and avoid conflicts with other images.
    /// </param>
    /// <returns>
    ///     A string representing the generated Docker image tag.
    /// </returns>
    private static string GenerateImageTag(string projectName, Guid projectId)
    {
        var safeProjectName = NormalizeContainerName(projectName);
        return $"automate-{safeProjectName}:{projectId.ToString()[..8]}";
    }


    /// <summary>
    ///     Normalizes a string to be used as a Docker container name by converting it to lowercase,
    ///     replacing invalid characters with hyphens, and ensuring it starts with a letter or number.
    /// </summary>
    /// <param name="value">The string to normalize.</param>
    /// <returns>The normalized container name.</returns>
    private static string NormalizeContainerName(string value)
    {
        var normalized = ContainerNameRegex().Replace(value.Trim().ToLowerInvariant(), "-");
        normalized = string.Join('-', normalized.Split('-',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return string.IsNullOrWhiteSpace(normalized) ? "automate-project" : normalized;
    }


    /// A regular expression to match any characters that are not lowercase letters or numbers, used for sanitizing container names.
    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex ContainerNameRegex();
}