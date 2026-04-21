using System.Net;
using System.Net.Sockets;
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
    /// <param name="csProjectId">
    ///     The GUID representing the unique identifier of the project to be deployed.
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
    public async Task<Deployment> DeployLocalProjectAsync(Guid csProjectId)
    {
        // Retrieve the C# project and its configuration from the database
        var csProject = await dbContext.CsProjects
            .Include(csp => csp.Configuration)
            .FirstOrDefaultAsync(csp => csp.Id == csProjectId);

        if (csProject?.Configuration == null)
            throw new InvalidOperationException($"Project or configuration not found for ID: {csProjectId}");

        var safeProjectName = csProject.Name.ToLowerInvariant().Replace(" ", "");
        var shortId = csProject.Id.ToString()[..8];
        var imageTag = $"automate-{safeProjectName}:{shortId}";
        var containerName = $"automate-{safeProjectName}-run";

        var deployment = new Deployment
        {
            CsProjectId = csProject.Id,
            Status = DeploymentStatus.Building,
            ImageTag = imageTag,
            DeployedAt = DateTimeOffset.UtcNow
        };

        // Save the deployment to the database
        dbContext.Deployments.Add(deployment);
        await dbContext.SaveChangesAsync();

        try
        {
            // Find the solution root and scan the project to gather necessary metadata for Dockerfile generation
            var solutionRoot = await systemScanner.FindSolutionRootAsync(csProject.Path);
            var metadata = await projectScanner.ScanProjectContentAsync(csProject.Path);

            // Generate Dockerfile and Dockerignore based on the project metadata
            var dockerfileContent = await templateService.GenerateDockerfileAsync(
                csProject.Name,
                metadata.DotNetVersion,
                csProject.Configuration.ExposedPort ?? 8080,
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

            // Determine an available port on the local machine to map to the container's exposed port
            var dynamicHostPort = GetAvailablePort();
            var containerInternalPort = csProject.Configuration.ExposedPort ?? 8080;

            // Update deployment status to "Starting" before attempting to run the container
            deployment.Status = DeploymentStatus.Starting;
            await dbContext.SaveChangesAsync();

            // Start the container using the generated image tag
            var containerId = await dockerService.StartContainerAsync(
                imageTag,
                containerName,
                dynamicHostPort,
                containerInternalPort,
                csProject.Configuration.EnvironmentVariablesJson);

            if (string.IsNullOrEmpty(containerId))
            {
                deployment.Status = DeploymentStatus.Failed;
                throw new Exception($"Failed to start container for project {csProject.Name}");
            }

            // Update deployment with the container ID and set status to "Running"
            deployment.DockerContainerId = containerId;
            deployment.Status = DeploymentStatus.Running;
            deployment.Logs = "Container started successfully, visit http://localhost:" + dynamicHostPort;
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


    /// <summary>
    ///     Determines and returns an available port on the local machine. This method creates a temporary
    ///     listener to identify a free port and then releases it for future use.
    /// </summary>
    /// <returns>
    ///     An integer representing a port number that is currently available for use.
    /// </returns>
    /// <exception cref="SocketException">
    ///     Thrown when an error occurs while accessing the network during the process of finding an available port.
    /// </exception>
    private int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}