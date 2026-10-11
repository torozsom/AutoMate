using System.Net;
using System.Net.Sockets;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.Docker;
using Application.Abstractions.Hosting;
using Application.Abstractions.Scanning;
using Application.Abstractions.Templating;
using Application.Diagnostics;
using Domain.DTO;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Docker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Orchestration;

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
    IDeploymentCapabilities capabilities,
    IDeploymentDiagnosticPublisher diagnostics,
    ILogger<LocalDeploymentOrchestrator> logger,
    IDeploymentStatusNotifier statusNotifier,
    ILocalDeploymentDiagnostics localDiagnostics)
    : ILocalDeploymentOrchestrator
{
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
        EnsureLocalDeploymentEnabled();
        ArgumentNullException.ThrowIfNull(config);

        using var projectScope = OperationalLog.BeginCorrelation(logger, projectId: config.ProjectId);
        logger.LogInformation("Starting local deployment process.");

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
        await DeploymentSnapshotCapture.CaptureAsync(dbContext, deployment, config, null, null, cancellationToken);
        dbContext.Deployments.Add(deployment);
        await dbContext.SaveChangesAsync(cancellationToken);
        using var activity = DeploymentTracing.Start(DeploymentOperation.LocalDeploy, config.ProjectId, deployment.Id);
        using var deploymentScope = OperationalLog.BeginCorrelation(logger, deployment.Id);
        statusNotifier.NotifyStatusChanged(config.ProjectId, deployment.Status);

        try
        {
            await PublishPhaseAsync(config.ProjectId, deployment.Id, "preparation",
                "Checking local deployment resources.", cancellationToken);
            await ValidateLocalResourcesAsync(config, cancellationToken);
            await ExecuteDeploymentStepsAsync(config, csProject, deployment, cancellationToken);
            DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Completed);
            return deployment;
        }
        catch (Exception ex)
        {
            DeploymentTracing.Finish(activity,
                ex is OperationCanceledException && cancellationToken.IsCancellationRequested
                    ? DeploymentTraceOutcome.Canceled
                    : DeploymentTraceOutcome.Failed);
            if (ex is DeploymentResourceConflictException conflict)
                try
                {
                    await diagnostics.PublishAsync(new DeploymentDiagnosticEvent(config.ProjectId, deployment.Id,
                            DeploymentDiagnosticSource.DockerCompose, DeploymentDiagnosticKind.Annotation,
                            DeploymentDiagnosticSeverity.Error, DateTimeOffset.UtcNow,
                            $"{conflict.Message}\r\n",
                            new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Build)),
                        CancellationToken.None);
                }
                catch (Exception diagnosticError)
                {
                    logger.LogWarning(diagnosticError, "Local resource conflict diagnostic unavailable: {FailureType}.",
                        diagnosticError.GetType().Name);
                }

            logger.LogError(ex, "Local deployment failed: {FailureType}.", ex.GetType().Name);
            await PublishPhaseAsync(config.ProjectId, deployment.Id, "outcome",
                "Local deployment failed. Review the preceding build and lifecycle diagnostics.",
                CancellationToken.None,
                DeploymentDiagnosticSeverity.Error);
            await _statusUpdater.SafeUpdateAsync(config.ProjectId, deployment, DeploymentStatus.Failed,
                CancellationToken.None);
            await localDiagnostics.SetOperationAsync(config.ProjectId, deployment.Id, false, cancellationToken);
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
        EnsureLocalDeploymentEnabled();
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

        var latestDeployment = await dbContext.Deployments.Include(d => d.CsProject!).ThenInclude(p => p.Application)
            .Where(d => d.CsProject!.AppId == projectId).OrderByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (latestDeployment?.CsProject?.Application is { } app)
            try
            {
                if (localDiagnostics.IsActive(projectId, latestDeployment.Id))
                {
                    await localDiagnostics.SetOperationAsync(projectId, latestDeployment.Id, true, cancellationToken);
                }
                else
                {
                    var config =
                        await projectScanner.AnalyzeDependenciesAsync(app, latestDeployment.CsProject,
                            cancellationToken);
                    await localDiagnostics.RegisterAsync(LocalDockerTargets.Create(config,
                        latestDeployment.CsProject.Name,
                        latestDeployment.Id), true, cancellationToken);
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Stop lifecycle collection unavailable: {FailureType}.",
                    exception.GetType().Name);
            }

        var isStopped = await dockerService.RunDockerComposeDownAsync(automateDir, projectName, projectId,
            cancellationToken, latestDeployment?.Id);

        if (isStopped)
        {
            // Persist the Docker outcome before cleanup so recovery cannot restore stopped deployment sources.
            if (latestDeployment != null && latestDeployment.Status != DeploymentStatus.Stopped)
            {
                await _statusUpdater.SafeUpdateAsync(projectId, latestDeployment, DeploymentStatus.Stopped,
                    cancellationToken);
                logger.LogInformation(
                    "[LocalDeploymentOrchestrator] Deployment stopped successfully for Project ID {Id}.", projectId);
            }

            // Allow final die/destroy observations to arrive before cancelling the sources.
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            await localDiagnostics.StopProjectAsync(projectId, cancellationToken);
            logger.LogInformation("Local diagnostic cleanup completed for project {ProjectId}.", projectId);
        }
        else
        {
            if (latestDeployment is not null)
                await localDiagnostics.SetOperationAsync(projectId, latestDeployment.Id, false, cancellationToken);
            logger.LogError("[LocalDeploymentOrchestrator] Failed to stop deployment for Project ID {Id}.", projectId);
            throw new InvalidOperationException("Docker Compose could not stop the deployment.");
        }
    }

    private void EnsureLocalDeploymentEnabled()
    {
        if (!capabilities.LocalDeploymentsEnabled)
            throw new InvalidOperationException("Local Docker deployments are disabled for this AutoMate instance.");
    }

    private async Task ValidateLocalResourcesAsync(DeploymentConfigDto config, CancellationToken cancellationToken)
    {
        var composeName = DockerNameNormalizer.NormalizeProjectName(config.ProjectName);
        var names = await dbContext.Applications.AsNoTracking()
            .Where(app => app.Id != config.ProjectId && app.SourceType == SourceType.Local)
            .Select(app => app.Name)
            .ToListAsync(cancellationToken);
        if (names.Any(name => DockerNameNormalizer.NormalizeProjectName(name) == composeName))
            throw new DeploymentResourceConflictException(
                $"Docker Compose name '{composeName}' is also used by another AutoMate project. Rename one project before deploying.");

        if (config.ExposedPort is < 1 or > 65535)
            throw new DeploymentResourceConflictException("The local host port must be between 1 and 65535.");

        // A redeploy may legitimately reuse its own currently bound port. The scheduler
        // separately reserves ports while different local deployments are in progress.
        var runningProjects = await dockerService.GetRunningProjectNamesAsync(cancellationToken);
        if (runningProjects.Contains(composeName, StringComparer.OrdinalIgnoreCase))
        {
            var ownsRunningDeployment = await dbContext.Deployments.AsNoTracking().AnyAsync(
                item => item.CsProject!.AppId == config.ProjectId && item.Status == DeploymentStatus.Running,
                cancellationToken);
            if (!ownsRunningDeployment)
                throw new DeploymentResourceConflictException(
                    $"Docker Compose name '{composeName}' is already running outside this project's active deployment.");
            return;
        }

        LocalHostPortAvailability.Check(config.ExposedPort);
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
        await PublishPhaseAsync(config.ProjectId, deployment.Id, "solution", "Locating the local solution root.",
            cancellationToken);
        logger.LogInformation("[LocalDeploymentOrchestrator] Step 1/4: Locating solution root.");
        var solutionRoot = await systemScanner.FindSolutionRootAsync(csProject.Path, cancellationToken);

        var automateDir = Path.Combine(solutionRoot, ".automate");
        if (!Directory.Exists(automateDir))
            Directory.CreateDirectory(automateDir);

        logger.LogInformation("[LocalDeploymentOrchestrator] Step 2/4: Scanning project content for dependencies...");
        var metadata = await projectScanner.ScanProjectContentAsync(csProject.Path, cancellationToken);
        DeploymentSnapshotCapture.Resolve(deployment, metadata.DotNetVersion);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "[LocalDeploymentOrchestrator] Step 3/4: Generating Infrastructure-as-Code files (Dockerfile, docker-compose)...");
        await templateService.GenerateAndSaveAllTemplatesAsync(config, metadata, csProject.Name, automateDir,
            cancellationToken);
        await PublishPhaseAsync(config.ProjectId, deployment.Id, "templates", "Local deployment files are ready.",
            cancellationToken);

        try
        {
            await localDiagnostics.RegisterAsync(LocalDockerTargets.Create(config, csProject.Name, deployment.Id), true,
                cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Deployment lifecycle collection unavailable: {FailureType}.", exception.GetType().Name);
            await PublishPhaseAsync(config.ProjectId, deployment.Id, "diagnostics",
                "Docker lifecycle collection is unavailable; deployment will continue.", cancellationToken);
        }

        logger.LogInformation("[LocalDeploymentOrchestrator] Step 4/4: Starting Docker Compose deployment...");
        await _statusUpdater.SafeUpdateAsync(config.ProjectId, deployment, DeploymentStatus.Starting,
            cancellationToken);

        var isDockerSuccess =
            await dockerService.RunDockerComposeUpAsync(automateDir, config.ProjectName, config.ProjectId,
                deployment.Id,
                cancellationToken);

        if (!isDockerSuccess)
            throw new InvalidOperationException("Docker Compose process returned an error or timed out. " +
                                                "Check server console for details.");

        var systemUrl = $"http://localhost:{config.ExposedPort}";
        deployment.ResolvedHostPort = config.ExposedPort;
        logger.LogInformation("Deployment finished successfully.");

        await _statusUpdater.SafeUpdateAsync(config.ProjectId, deployment, DeploymentStatus.Running,
            cancellationToken);

        await localDiagnostics.SetOperationAsync(config.ProjectId, deployment.Id, false, cancellationToken);
    }

    /// <summary>Emits safe deployment phases without exposing source paths or letting diagnostics fail deployment work.</summary>
    private async Task PublishPhaseAsync(Guid projectId, Guid deploymentId, string phase, string message,
        CancellationToken token, DeploymentDiagnosticSeverity severity = DeploymentDiagnosticSeverity.Information)
    {
        try
        {
            await diagnostics.PublishAsync(new DeploymentDiagnosticEvent(projectId, deploymentId,
                DeploymentDiagnosticSource.AutoMate,
                DeploymentDiagnosticKind.Lifecycle, severity, DateTimeOffset.UtcNow,
                $"[Local deployment] {message}\r\n", new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Build),
                new Dictionary<string, string> { ["phase"] = phase }), token);
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Local phase diagnostic unavailable: {FailureType}.",
                exception.GetType().Name);
        }
    }
}

internal static class LocalHostPortAvailability
{
    public static void Check(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
        }
        catch (SocketException ex)
        {
            throw new DeploymentResourceConflictException(
                $"Host port {port} is already in use. Choose another port for this deployment.", ex);
        }
    }
}