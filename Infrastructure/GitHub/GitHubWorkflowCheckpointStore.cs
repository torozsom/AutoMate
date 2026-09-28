using Domain.Entities;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.GitHub;

/// <summary>Persists non-sensitive workflow streaming checkpoints for restart-safe deduplication.</summary>
internal sealed class GitHubWorkflowCheckpointStore(AutoMateDbContext dbContext)
{
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
        workflowCheckpoint.JobCheckpoints.Add(checkpoint);
        await dbContext.SaveChangesAsync(cancellationToken);
        return checkpoint;
    }

    public Task SaveAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}
