using Core.DTO;
using Core.Entities;

namespace Services.Orchestration;

/// <summary>
///     Service responsible for coordinating the end-to-end deployment process
///     of local .NET projects, utilizing scanners, templating, and Docker services.
/// </summary>
public interface ILocalDeploymentOrchestrator
{
    /// <summary>
    ///     Initiates the deployment process for a local .NET project.
    /// </summary>
    /// <param name="config">The deployment configuration for the selected local project.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>The created deployment record.</returns>
    Task<Deployment> DeployLocalProjectAsync(DeploymentConfigDto config, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stops an existing local Docker Compose deployment.
    /// </summary>
    /// <param name="projectId">The application/project ID whose deployment should stop.</param>
    /// <param name="projectName">The project name used as the Docker Compose project name source.</param>
    /// <param name="csProjectPath">The C# project path used to locate the generated .automate directory.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    Task StopDeploymentAsync(Guid projectId, string projectName, string csProjectPath,
        CancellationToken cancellationToken = default);
}