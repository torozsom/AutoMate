using Application.Abstractions.Docker;
using Domain.Enums.Deployments;
using Domain.Enums.Projects;
using Infrastructure.ApplicationServices.Orchestration.CloudPreparation;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.ApplicationServices.Orchestration.Lifecycle;

/// <summary>
///     Reconciles verified local deployments against Docker while leaving remote Azure history unchanged.
/// </summary>
/// <param name="dbContext">The operation-owned metadata context.</param>
/// <param name="dockerService">Reads the current local Compose inventory.</param>
/// <param name="logger">Preserves the hosted cleanup category and operational events.</param>
internal sealed class DeploymentReconciliation(
    AutoMateDbContext dbContext,
    IDockerService dockerService,
    ILogger<DeploymentCleanupHostedService> logger)
{
    /// <summary>
    ///     Applies the existing startup status transitions and Docker name comparison rules.
    /// </summary>
    /// <param name="cancellationToken">The existing startup operation token.</param>
    /// <returns>Completes when reconciliation is persisted; the host translates failures and cancellation.</returns>
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        // 1. Mark 'Starting' as 'Failed'
        var updatedCount = await dbContext.Deployments
            .Where(d => d.Status == DeploymentStatus.Starting
                        && d.CsProject!.Application.SourceType == SourceType.Local
                        && d.CloudResourceId == null
                        && d.CloudGitHubActionRunId == null)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(d => d.Status, DeploymentStatus.Failed),
                cancellationToken);

        if (updatedCount > 0)
            logger.LogWarning("[DeploymentCleanupHostedService] Successfully cleaned up and marked {Count} " +
                              "stuck deployments (Starting) as 'Failed'.", updatedCount);

        // 2. Synchronize 'Running' and 'Stopped' deployments with Docker daemon
        var runningDeployments = await dbContext.Deployments
            .Include(d => d.CsProject)
            .ThenInclude(csp => csp!.Application)
            .Where(d => (d.Status == DeploymentStatus.Running
                         || d.Status == DeploymentStatus.Stopped)
                        && d.CsProject!.Application.SourceType == SourceType.Local
                        && d.CloudResourceId == null
                        && d.CloudGitHubActionRunId == null)
            .ToListAsync(cancellationToken);

        if (runningDeployments.Count > 0)
        {
            var runningProjectsInDocker = await dockerService.GetRunningProjectNamesAsync(cancellationToken);
            var changedCount = 0;

            foreach (var deployment in runningDeployments)
            {
                if (deployment.CsProject == null) continue;

                var expectedProjectName = OrchestrationNameNormalizer.NormalizeComposeProjectName(
                    deployment.CsProject is { Application: { } application }
                        ? application.Name
                        : deployment.CsProject.Name);
                var isActuallyRunning =
                    runningProjectsInDocker.Contains(expectedProjectName, StringComparer.OrdinalIgnoreCase);

                switch (deployment.Status)
                {
                    case DeploymentStatus.Running when !isActuallyRunning:
                        deployment.Status = DeploymentStatus.Stopped;
                        changedCount++;
                        break;
                    case DeploymentStatus.Stopped when isActuallyRunning:
                        deployment.Status = DeploymentStatus.Running;
                        changedCount++;
                        break;
                    case DeploymentStatus.Running:
                    case DeploymentStatus.Stopped:
                    case DeploymentStatus.Starting:
                    case DeploymentStatus.Failed:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(
                            "Unexpected deployment status encountered during reconciliation: "
                            + deployment.Status);
                }
            }

            if (changedCount > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                logger.LogWarning("[DeploymentCleanupHostedService] Synchronized {Count} deployments with " +
                                  "the actual Docker state.", changedCount);
            }
        }

        logger.LogInformation("[DeploymentCleanupHostedService] Deployment sync completed.");
    }
}
