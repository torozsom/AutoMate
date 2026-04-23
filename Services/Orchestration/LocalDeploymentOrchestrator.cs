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
        logger.LogInformation("Starting Deployment Process for {Project}", config.ProjectName);

        var csProject = await dbContext.CsProjects
            .FirstOrDefaultAsync(csp => csp.Id == config.CsProjectId);

        if (csProject == null)
        {
            logger.LogError("Database record for CsProject {Id} not found!", config.CsProjectId);
            throw new InvalidOperationException("Project not found.");
        }

        var deployment = new Deployment
        {
            CsProjectId = csProject.Id,
            Status = DeploymentStatus.Building,
            ImageTag = $"automate-{csProject.Name.ToLower()}:{csProject.Id.ToString()[..8]}"
        };

        // Save the deployment to the database
        dbContext.Deployments.Add(deployment);
        await dbContext.SaveChangesAsync();

        try
        {
            logger.LogInformation("Step 1: Locating solution root...");
            var solutionRoot = await systemScanner.FindSolutionRootAsync(csProject.Path);
            logger.LogInformation("Solution root found at: {Path}", solutionRoot);

            logger.LogInformation("Step 2: Scanning project content for metadata...");
            var metadata = await projectScanner.ScanProjectContentAsync(csProject.Path);

            logger.LogInformation("Step 3: Generating Infrastructure-as-Code files...");
            var dockerfileContent = await templateService.GenerateDockerfileAsync(
                csProject.Name, metadata.DotNetVersion, 8080, metadata.AllProjectPaths, solutionRoot);
            var dockerIgnoreContent = await templateService.GenerateDockerIgnoreAsync();
            var composeContent = await templateService.GenerateDockerComposeAsync(config);

            logger.LogInformation("Step 4: Saving files to disk...");
            await templateService.SaveFileAsync(solutionRoot, "Dockerfile", dockerfileContent);
            await templateService.SaveFileAsync(solutionRoot, ".dockerignore", dockerIgnoreContent);
            await templateService.SaveFileAsync(solutionRoot, "docker-compose.yml", composeContent);
            logger.LogInformation("Configuration files saved successfully.");

            logger.LogInformation("Step 5: Invoking Docker Compose...");
            deployment.Status = DeploymentStatus.Starting;
            await dbContext.SaveChangesAsync();

            var success = await dockerService.RunDockerComposeUpAsync(solutionRoot, config.ProjectName);

            if (!success)
            {
                deployment.Status = DeploymentStatus.Failed;
                await dbContext.SaveChangesAsync();
                logger.LogError("Step 5 Failed: Docker Compose could not start the services.");
                throw new Exception("Docker Compose process returned an error. Check server logs.");
            }

            logger.LogInformation("Step 6: Finalizing deployment record...");
            deployment.Status = DeploymentStatus.Running;
            deployment.Logs = $"System live at http://localhost:{config.ExposedPort}";
            await dbContext.SaveChangesAsync();

            logger.LogInformation("--- Deployment Finished Successfully ---");
            return deployment;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deployment failed during execution.");
            deployment.Status = DeploymentStatus.Failed;
            deployment.Logs = $"Error: {ex.Message}";
            await dbContext.SaveChangesAsync();
            throw;
        }
    }
}