using System.Net;
using System.Net.Sockets;
using Application.Abstractions.Diagnostics.Events;
using Application.Abstractions.Docker;
using Application.Abstractions.Hosting;
using Application.Abstractions.Scanning;
using Application.Abstractions.Templating;
using Application.Diagnostics;
using Application.Orchestration.Jobs;
using Application.Orchestration.Local;
using Application.Orchestration.Status;
using Domain.DTO.Deployment;
using Domain.Entities.Deployments;
using Domain.Entities.Projects;
using Domain.Enums.Deployments;
using Domain.Enums.Projects;
using Infrastructure.ApplicationServices.Orchestration.CloudPreparation;
using Infrastructure.ApplicationServices.Orchestration.Lifecycle;
using Infrastructure.Data;
using Infrastructure.Docker.Compose;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.ApplicationServices.Orchestration.Local;

/// <summary>
///     Orchestrates the process of deploying .NET projects locally by managing interactions
///     with various services, including database, system scanning, project scanning,
///     templating, and Docker.
/// </summary>
/// <param name="dbContext">
///     Application metadata context used for ownership, leases and persisted state.
/// </param>
/// <param name="systemScanner">
///     Provides the local system scanner service boundary used by this operation.
/// </param>
/// <param name="projectScanner">
///     Provides the project scanner service boundary used by this operation.
/// </param>
/// <param name="templateService">Provides the templating service boundary used by this operation.</param>
/// <param name="dockerService">Provides the docker service boundary used by this operation.</param>
/// <param name="capabilities">Hosting-profile capability flags used to gate provider operations.</param>
/// <param name="diagnostics">Accepts normalized observations for redaction and durable delivery.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
/// <param name="statusNotifier">Publishes deployment transitions to in-process subscribers.</param>
/// <param name="localDiagnostics">
///     Provides the local deployment diagnostics boundary used by this operation.
/// </param>
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
    /// <summary>Handles deployment status persistence and UI notifications.</summary>
    private readonly DeploymentStatusUpdater _statusUpdater =
        new(dbContext, statusNotifier, logger, nameof(LocalDeploymentOrchestrator));

    /// <summary>
    ///     Deploys a local .NET project by orchestrating the entire process.
    /// </summary>
    /// <param name="config">The configuration for the deployment.</param>
    /// <param name="cancellationToken">
    ///     A cancellation token that can be used to cancel the operation if needed.
    /// </param>
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
    /// <exception cref="ArgumentNullException">A required input or dependency is null.</exception>
    public async Task<Deployment> DeployLocalProjectAsync(DeploymentConfigDto config,
        CancellationToken cancellationToken = default)
    {
        EnsureLocalDeploymentEnabled();
        ArgumentNullException.ThrowIfNull(config);

        using var projectScope = OperationalLog.BeginCorrelation(logger, projectId: config.ProjectId);
        logger.LogInformation("Starting local deployment process.");

        var csProject = await dbContext.CsProjects
            .FirstOrDefaultAsync(csp => csp.Id == config.CsProjectId, cancellationToken);

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
    /// <param name="cancellationToken">
    ///     A cancellation token that can be used to cancel the operation if needed.
    /// </param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    /// <exception cref="InvalidOperationException">Docker Compose could not stop the deployment.</exception>
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

        var latestDeployment = await dbContext.Deployments
            .Include(d => d.CsProject!)
            .ThenInclude(p => p.Application)
            .Where(d => d.CsProject!.AppId == projectId)
            .OrderByDescending(d => d.CreatedAt)
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

        var isStopped = await dockerService.RunDockerComposeDownAsync(automateDir, projectName,
            projectId, latestDeployment?.Id, cancellationToken);

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

    /// <summary>
    ///     Rejects local deployment work when the selected hosting profile disables Docker
    ///     operations.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Local Docker deployments are disabled for this AutoMate instance.
    /// </exception>
    private void EnsureLocalDeploymentEnabled()
    {
        if (!capabilities.LocalDeploymentsEnabled)
            throw new InvalidOperationException("Local Docker deployments are disabled for this AutoMate instance.");
    }

    /// <summary>
    ///     Rejects conflicting Compose identities or occupied local host ports before deployment
    ///     side effects.
    /// </summary>
    /// <param name="config">The deployment configuration used by the operation.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    /// <exception cref="DeploymentResourceConflictException">
    ///     Another project owns the Compose identity or the selected host port is unavailable.
    /// </exception>
    private async Task ValidateLocalResourcesAsync(DeploymentConfigDto config, CancellationToken cancellationToken)
    {
        var composeName = DockerNameNormalizer.NormalizeProjectName(config.ProjectName);
        var names = await dbContext.Applications
            .AsNoTracking()
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
    ///     scanning the project for dependencies, generating necessary templates, and running
    ///     Docker Compose.
    /// </summary>
    /// <param name="config"></param>
    /// <param name="csProject"></param>
    /// <param name="deployment">Tracked deployment metadata used for this exact launch or history scope.</param>
    /// <param name="cancellationToken">
    ///     A cancellation token that can be used to cancel the operation if needed.
    /// </param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
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

        deployment.ResolvedHostPort = config.ExposedPort;
        logger.LogInformation("Deployment finished successfully.");

        await _statusUpdater.SafeUpdateAsync(config.ProjectId, deployment, DeploymentStatus.Running,
            cancellationToken);

        await localDiagnostics.SetOperationAsync(config.ProjectId, deployment.Id, false, cancellationToken);
    }

    /// <summary>
    ///     Emits safe deployment phases without exposing source paths or letting diagnostics fail
    ///     deployment work.
    /// </summary>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="deploymentId">
    ///     The deployment identity; null is used only where legacy observations permit it.
    /// </param>
    /// <param name="phase">The recorded cloud-run phase.</param>
    /// <param name="message">The message supplied to the operation.</param>
    /// <param name="token">Cancels the underlying operation when observed.</param>
    /// <param name="severity">The normalized severity used to rank a context group.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
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

/// <summary>
///     Checks whether a local TCP port can be acquired without leaving a listener running.
/// </summary>
internal static class LocalHostPortAvailability
{
    /// <summary>
    ///     Temporarily probes a requested local TCP port and releases the probe immediately.
    /// </summary>
    /// <param name="port">Host port checked for availability.</param>
    /// <exception cref="DeploymentResourceConflictException">
    ///     The requested local host port cannot be acquired safely.
    /// </exception>
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
