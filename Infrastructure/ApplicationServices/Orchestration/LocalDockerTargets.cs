using Application.Abstractions.Docker;
using Domain.DTO;
using Infrastructure.Docker;

namespace Application.Orchestration;

/// <summary>Captures collector inventories using the existing generated container naming rules.</summary>
internal static class LocalDockerTargets
{
    /// <summary>Captures only non-secret project, deployment and service identities.</summary>
    internal static DockerDeploymentTarget Create(DeploymentConfigDto config, string projectName, Guid deploymentId)
    {
        var app = OrchestrationNameNormalizer.NormalizeContainerName(config.ProjectName);
        var containers = new List<DockerContainerTarget>
            { new($"{OrchestrationNameNormalizer.NormalizeContainerName(projectName)}-web", "web") };
        if (config.Databases is not null)
            containers.AddRange(config.Databases.Select(db =>
                new DockerContainerTarget($"{app}-{db.ContainerNameSuffix}", db.ContainerNameSuffix, true)));
        return new DockerDeploymentTarget(config.ProjectId, deploymentId,
            DockerNameNormalizer.NormalizeProjectName(config.ProjectName), containers, DateTimeOffset.UtcNow);
    }
}