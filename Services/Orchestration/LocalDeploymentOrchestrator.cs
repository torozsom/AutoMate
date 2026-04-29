using Core.DTO;
using Core.Entities;
using Core.Enums;
using Microsoft.EntityFrameworkCore;
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
public class LocalDeploymentOrchestrator(
    AutoMateDbContext dbContext,
    ILocalSystemScannerService systemScanner,
    IProjectScannerService projectScanner,
    ITemplateService templateService,
    IDockerService dockerService,
    ILogger<LocalDeploymentOrchestrator> logger)
    : ILocalDeploymentOrchestrator
{
    /// <summary>
    ///     Deploys a local .NET project by orchestrating the entire process.
    /// </summary>
    /// <param name="config">The configuration for the deployment.</param>
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
    public async Task<Deployment> DeployLocalProjectAsync(DeploymentConfigDto config)
    {
        logger.LogInformation(
            "[LocalDeploymentOrchestrator] Starting Deployment Process for project '{ProjectName}'...",
            config.ProjectName);

        var csProject = await dbContext.CsProjects.FirstOrDefaultAsync(csp => csp.Id == config.CsProjectId);

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
            Status = DeploymentStatus.Building,
            ImageTag = GenerateImageTag(csProject.Name, csProject.Id)
        };

        // Save the deployment to the database
        dbContext.Deployments.Add(deployment);
        await dbContext.SaveChangesAsync();

        try
        {
            await ExecuteDeploymentStepsAsync(config, csProject, deployment);
            return deployment;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "[LocalDeploymentOrchestrator] Deployment failed during execution for project '{ProjectName}'.",
                config.ProjectName);
            await SafeUpdateDeploymentStatusAsync(deployment, DeploymentStatus.Failed, $"Error: {ex.Message}");
            throw;
        }
    }


    /// <summary>
    ///     Executes the main steps of the deployment process, including locating the solution root,
    ///     scanning the project for dependencies, generating necessary templates, and running Docker Compose.
    /// </summary>
    /// <param name="config"></param>
    /// <param name="csProject"></param>
    /// <param name="deployment"></param>
    /// <exception cref="InvalidOperationException"></exception>
    private async Task ExecuteDeploymentStepsAsync(DeploymentConfigDto config, CsProject csProject,
        Deployment deployment)
    {
        logger.LogInformation("[LocalDeploymentOrchestrator] Step 1/4: Locating solution root for {Path}...",
            csProject.Path);
        var solutionRoot = await systemScanner.FindSolutionRootAsync(csProject.Path);

        var automateDir = Path.Combine(solutionRoot, ".automate");
        if (!Directory.Exists(automateDir))
            Directory.CreateDirectory(automateDir);

        logger.LogInformation("[LocalDeploymentOrchestrator] Step 2/4: Scanning project content for dependencies...");
        var metadata = await projectScanner.ScanProjectContentAsync(csProject.Path);

        logger.LogInformation(
            "[LocalDeploymentOrchestrator] Step 3/4: Generating Infrastructure-as-Code files (Dockerfile, docker-compose)...");
        await templateService.GenerateAndSaveAllTemplatesAsync(config, metadata, csProject.Name, automateDir);

        logger.LogInformation("[LocalDeploymentOrchestrator] Step 4/4: Starting Docker Compose deployment...");
        await SafeUpdateDeploymentStatusAsync(deployment, DeploymentStatus.Starting);

        var isDockerSuccess = await dockerService.RunDockerComposeUpAsync(automateDir, config.ProjectName);

        if (!isDockerSuccess)
            throw new InvalidOperationException("Docker Compose process returned an error or timed out. " +
                                                "Check server console for details.");

        var systemUrl = $"http://localhost:{config.ExposedPort}";
        logger.LogInformation("--- Deployment Finished Successfully! System live at {Url} ---", systemUrl);

        await SafeUpdateDeploymentStatusAsync(deployment, DeploymentStatus.Running, $"System live at {systemUrl}");
    }


    /// <summary>
    ///     Safely updates the deployment status in the database, handling any
    ///     potential exceptions that may occur during the update process.
    /// </summary>
    /// <param name="deployment">
    ///     The <see cref="Deployment" /> entity whose status is to be updated.
    /// </param>
    /// <param name="status">
    ///     The new <see cref="DeploymentStatus" /> value to set for the deployment.
    /// </param>
    /// <param name="logs">
    ///     Optional log messages or details to be saved with the deployment status update.
    /// </param>
    private async Task SafeUpdateDeploymentStatusAsync(Deployment deployment, DeploymentStatus status,
        string? logs = null)
    {
        try
        {
            deployment.Status = status;
            if (logs != null)
                deployment.Logs = logs;

            await dbContext.SaveChangesAsync();
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
        var safeProjectName = projectName.ToLowerInvariant().Replace(" ", "-").Replace(".", "-");
        return $"automate-{safeProjectName}:{projectId.ToString()[..8]}";
    }
}