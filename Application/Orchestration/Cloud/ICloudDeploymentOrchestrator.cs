using Domain.DTO.Deployment;
using Domain.Entities.Deployments;

namespace Application.Orchestration.Cloud;

/// <summary>
///     Service responsible for coordinating cloud deployment asset generation and GitHub commit
///     workflow.
/// </summary>
public interface ICloudDeploymentOrchestrator
{
    /// <summary>
    ///     Generates cloud deployment files and commits them to the configured GitHub repository
    ///     branch.
    /// </summary>
    /// <param name="request">The cloud deployment request.</param>
    /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
    /// <returns>A task returning the created deployment record.</returns>
    Task<Deployment> DeployCloudProjectAsync(CloudDeploymentRequestDto request,
        CancellationToken cancellationToken = default);
}
