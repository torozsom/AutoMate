using Core.DTO;
using Core.Entities;

namespace Services.Orchestration;

/// <summary>
///     Service responsible for coordinating the end-to-end deployment process
///     of local .NET projects, utilizing scanners, templating, and Docker services.
/// </summary>
public interface ILocalDeploymentOrchestrator
{
    /// Initiates the deployment process for a local .NET project.
    Task<Deployment> DeployLocalProjectAsync(DeploymentConfigDto config);

    /// Stops an existing deployment for a local .NET project.
    Task StopDeploymentAsync(Guid projectId, string projectName, string csProjectPath);
}