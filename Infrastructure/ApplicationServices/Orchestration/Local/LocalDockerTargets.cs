using Application.Abstractions.Docker;
using Domain.DTO.Deployment;
using Infrastructure.ApplicationServices.Orchestration.CloudPreparation;
using Infrastructure.Docker.Compose;

namespace Infrastructure.ApplicationServices.Orchestration.Local;

/// <summary>
///     Captures collector inventories using the existing generated container naming rules.
/// </summary>
internal static class LocalDockerTargets
{
    /// <summary>
    ///     Captures only non-secret project, deployment and service identities.
    /// </summary>
    /// <param name="config">The deployment configuration used by the operation.</param>
    /// <param name="projectName">The project name used for deployment artifacts or Compose lookup.</param>
    /// <param name="deploymentId">
    ///     The deployment identity; null is used only where legacy observations permit it.
    /// </param>
    /// <returns>The docker deployment target projection derived from the verified inputs.</returns>
    internal static DockerDeploymentTarget Create(DeploymentConfigDto config, string projectName, Guid deploymentId)
    {
        var app = OrchestrationNameNormalizer.NormalizeContainerName(config.ProjectName);
        var containers = new List<DockerContainerTarget>
            { new($"{OrchestrationNameNormalizer.NormalizeContainerName(projectName)}-web", "web") };

        if (config is { Databases: { } databases })
            containers.AddRange(databases.Select(db =>
                new DockerContainerTarget($"{app}-{db.ContainerNameSuffix}", db.ContainerNameSuffix, true)));

        return new DockerDeploymentTarget(config.ProjectId, deploymentId,
            DockerNameNormalizer.NormalizeProjectName(config.ProjectName), containers, DateTimeOffset.UtcNow);
    }
}
