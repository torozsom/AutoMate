using System.Text.Json;
using Core.DTO;
using Core.Entities;
using Core.Enums;
using Microsoft.EntityFrameworkCore;
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
    IDockerService dockerService)
    : ILocalDeploymentOrchestrator
{
    /// <summary>
    ///     Deploys a local project using the provided project ID. The method handles the process
    ///     of building a Docker image and container for the project and updating the deployment status.
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
        var csProject = await dbContext.CsProjects
            .FirstOrDefaultAsync(csp => csp.Id == config.CsProjectId);

        if (csProject == null)
            throw new InvalidOperationException($"Project not found for ID: {config.CsProjectId}");

        var safeProjectName = csProject.Name.ToLowerInvariant().Replace(" ", "");
        var shortId = csProject.Id.ToString()[..8];
        var imageTag = $"automate-{safeProjectName}:{shortId}";
        var containerName = $"automate-{safeProjectName}-run";

        var deployment = new Deployment
        {
            CsProjectId = csProject.Id,
            Status = DeploymentStatus.Building,
            ImageTag = imageTag
        };

        // Save the deployment to the database
        dbContext.Deployments.Add(deployment);
        await dbContext.SaveChangesAsync();

        try
        {
            // Find the solution root and scan the project to gather necessary metadata for Dockerfile generation
            var solutionRoot = await systemScanner.FindSolutionRootAsync(csProject.Path);
            var metadata = await projectScanner.ScanProjectContentAsync(csProject.Path);

            // Generate Dockerfile and .dockerignore content based on the scanned metadata and save them to the solution root
            var dockerfileContent = await templateService.GenerateDockerfileAsync(
                csProject.Name,
                metadata.DotNetVersion,
                config.ExposedPort,
                metadata.AllProjectPaths,
                solutionRoot);

            var dockerIgnoreContent = await templateService.GenerateDockerIgnoreAsync();

            // Save the Dockerfile and Dockerignore to the project root directory
            await templateService.SaveFileAsync(solutionRoot, "Dockerfile", dockerfileContent);
            await templateService.SaveFileAsync(solutionRoot, ".dockerignore", dockerIgnoreContent);

            // Build the Docker image using the generated Dockerfile
            var buildSuccess = await dockerService.BuildImageAsync(solutionRoot, imageTag);
            if (!buildSuccess)
            {
                deployment.Status = DeploymentStatus.Failed;
                throw new Exception($"Failed to build Docker image for project {csProject.Name}");
            }

            deployment.Status = DeploymentStatus.Starting;
            await dbContext.SaveChangesAsync();

            // Serialize the custom environment variables to JSON format to pass them to the Docker container
            var envVarsJson = JsonSerializer.Serialize(config.CustomEnvVars);

            // Start the Docker container with the specified image, ports, and environment variables
            var containerId = await dockerService.StartContainerAsync(
                imageTag,
                containerName,
                config.ExposedPort,
                8080,
                envVarsJson
            );

            if (string.IsNullOrEmpty(containerId))
            {
                deployment.Status = DeploymentStatus.Failed;
                throw new Exception($"Failed to start container for project {csProject.Name}");
            }

            deployment.DockerContainerId = containerId;
            deployment.Status = DeploymentStatus.Running;
            deployment.Logs = "Container started successfully, visit http://localhost:" + config.ExposedPort;
            await dbContext.SaveChangesAsync();

            return deployment;
        }
        catch (Exception ex)
        {
            deployment.Status = DeploymentStatus.Failed;
            deployment.Logs = $"Deployment failed: {ex.Message}";
            Console.WriteLine(ex.Message);
            await dbContext.SaveChangesAsync();
            throw;
        }
    }
}