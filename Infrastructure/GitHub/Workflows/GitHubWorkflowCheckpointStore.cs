using Domain.Entities.Diagnostics;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.GitHub.Workflows;

/// <summary>
///     Persists non-sensitive workflow streaming checkpoints for restart-safe deduplication.
/// </summary>
/// <param name="dbContext">
///     Application metadata context used for ownership, leases and persisted state.
/// </param>
internal sealed class GitHubWorkflowCheckpointStore(AutoMateDbContext dbContext)
{
    /// <summary>
    ///     Loads or creates the checkpoint for one deployment, workflow run, and attempt.
    /// </summary>
    /// <param name="deploymentId">
    ///     The deployment identity; null is used only where legacy observations permit it.
    /// </param>
    /// <param name="workflowRunId">Verified GitHub Actions run identifier.</param>
    /// <param name="workflowAttempt">GitHub workflow rerun attempt number.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning the git hub workflow checkpoint projection derived from the verified
    ///     inputs.
    /// </returns>
    public async Task<GitHubWorkflowCheckpoint> GetOrCreateWorkflowAsync(Guid deploymentId, long workflowRunId,
        int workflowAttempt, CancellationToken cancellationToken)
    {
        var checkpoint = await dbContext.GitHubWorkflowCheckpoints
            .Include(item => item.JobCheckpoints)
            .SingleOrDefaultAsync(item => item.DeploymentId == deploymentId && item.WorkflowRunId == workflowRunId &&
                                          item.WorkflowAttempt == workflowAttempt, cancellationToken);
        if (checkpoint is not null) return checkpoint;

        checkpoint = new GitHubWorkflowCheckpoint
        {
            DeploymentId = deploymentId,
            WorkflowRunId = workflowRunId,
            WorkflowAttempt = workflowAttempt
        };
        dbContext.GitHubWorkflowCheckpoints.Add(checkpoint);
        await dbContext.SaveChangesAsync(cancellationToken);
        return checkpoint;
    }

    /// <summary>
    ///     Loads or inserts a job checkpoint associated with the given workflow checkpoint.
    /// </summary>
    /// <param name="workflowCheckpoint">Durable per-workflow cursor and state metadata.</param>
    /// <param name="jobId">The GitHub workflow job identity.</param>
    /// <param name="jobName">Provider job name used to match archive entries.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning the git hub workflow job checkpoint projection derived from the
    ///     verified inputs.
    /// </returns>
    public async Task<GitHubWorkflowJobCheckpoint> GetOrCreateJobAsync(GitHubWorkflowCheckpoint workflowCheckpoint,
        long jobId, string jobName, CancellationToken cancellationToken)
    {
        var checkpoint = workflowCheckpoint.JobCheckpoints.SingleOrDefault(item => item.JobId == jobId);
        if (checkpoint is not null) return checkpoint;

        checkpoint = new GitHubWorkflowJobCheckpoint
        {
            GitHubWorkflowCheckpointId = workflowCheckpoint.Id,
            JobId = jobId,
            JobName = jobName
        };
        dbContext.Set<GitHubWorkflowJobCheckpoint>().Add(checkpoint);
        await dbContext.SaveChangesAsync(cancellationToken);
        return checkpoint;
    }

    /// <summary>
    ///     Persists changed state and log cursors.
    /// </summary>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    public Task SaveAsync(CancellationToken cancellationToken)
    {
        return dbContext.SaveChangesAsync(cancellationToken);
    }
}
