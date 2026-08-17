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
public sealed class LocalDeploymentOrchestrator(
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
    /// <summary>
    ///     Manages background Docker log and metric streaming workers.
    /// </summary>
    private readonly LocalDeploymentLogStreamManager _logStreamManager = new(serviceScopeFactory, logger);

    /// <summary>
    ///     Handles deployment status persistence and UI notifications.
    /// </summary>
    private readonly DeploymentStatusUpdater _statusUpdater =
        new(dbContext, statusNotifier, logger, nameof(LocalDeploymentOrchestrator));

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
            ImageTag = OrchestrationNameNormalizer.GenerateImageTag(csProject.Name, csProject.Id)
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
            await _statusUpdater.SafeUpdateAsync(config.ProjectId, deployment, DeploymentStatus.Failed,
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
            await _logStreamManager.StopAsync(projectId);

            var latestDeployment = await dbContext.Deployments
                .Where(d => d.CsProject!.AppId == projectId)
                .OrderByDescending(d => d.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (latestDeployment != null && latestDeployment.Status != DeploymentStatus.Stopped)
            {
                await _statusUpdater.SafeUpdateAsync(projectId, latestDeployment, DeploymentStatus.Stopped,
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
        await _statusUpdater.SafeUpdateAsync(config.ProjectId, deployment, DeploymentStatus.Starting,
            cancellationToken);

        var isDockerSuccess =
            await dockerService.RunDockerComposeUpAsync(automateDir, config.ProjectName, config.ProjectId,
                cancellationToken);

        if (!isDockerSuccess)
            throw new InvalidOperationException("Docker Compose process returned an error or timed out. " +
                                                "Check server console for details.");

        var systemUrl = $"http://localhost:{config.ExposedPort}";
        logger.LogInformation("--- Deployment Finished Successfully! System live at {Url} ---", systemUrl);

        await _statusUpdater.SafeUpdateAsync(config.ProjectId, deployment, DeploymentStatus.Running,
            cancellationToken);

        _logStreamManager.Start(config, csProject);
    }
}