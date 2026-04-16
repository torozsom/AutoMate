using Core.Entities;
using Core.Enums;
using Microsoft.EntityFrameworkCore;
using Services.Data;
using Services.Docker;
using Services.Scanner;
using Services.Templating;

namespace Services.Orchestration;

public class LocalDeploymentOrchestrator(AutoMateDbContext dbContext, ILocalSystemScannerService systemScanner,
    IProjectScannerService projectScanner, ITemplateService templateService, IDockerService dockerService)
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

        dbContext.Deployments.Add(deployment);
        await dbContext.SaveChangesAsync();

        try
        {
            var solutionRoot = await systemScanner.FindSolutionRootAsync(csProject.Path);
            var metadata = await projectScanner.ScanProjectContentAsync(csProject.Path);

            var dockerfileContent = await templateService.GenerateDockerfileAsync(
                csProject.Name,
                metadata.DotNetVersion,
                csProject.Configuration.ExposedPort ?? 8080,
                metadata.AllProjectPaths,
                solutionRoot);

            var dockerIgnoreContent = await templateService.GenerateDockerIgnoreAsync();
            await templateService.SaveFileAsync(solutionRoot, "Dockerfile", dockerfileContent);
            await templateService.SaveFileAsync(solutionRoot, ".dockerignore", dockerIgnoreContent);

            var buildSuccess = await dockerService.BuildImageAsync(solutionRoot, imageTag);
            if (!buildSuccess)
            {
                deployment.Status = DeploymentStatus.Failed;
                throw new Exception($"Failed to build Docker image for project {csProject.Name}");
            }

            deployment.Status = DeploymentStatus.Starting;
            await dbContext.SaveChangesAsync();

            var containerId = await dockerService.StartContainerAsync(
                imageTag,
                containerName,
                csProject.Configuration.ExposedPort ?? 8080,
                csProject.Configuration.ExposedPort ?? 8080,
                csProject.Configuration.EnvironmentVariablesJson);

            if (string.IsNullOrEmpty(containerId))
            {
                deployment.Status = DeploymentStatus.Failed;
                throw new Exception($"Failed to start container for project {csProject.Name}");
            }

            deployment.DockerContainerId = containerId;
            deployment.Status = DeploymentStatus.Running;
            await dbContext.SaveChangesAsync();

            return deployment;
        }
        catch (Exception ex)
        {
            deployment.Status = DeploymentStatus.Failed;
            deployment.Logs = $"Deployment failed: {ex.Message}";
            await dbContext.SaveChangesAsync();
            throw;
        }
    }
}