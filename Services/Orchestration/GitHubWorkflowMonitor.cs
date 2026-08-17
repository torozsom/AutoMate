using Core.DTO;
using Microsoft.Extensions.Logging;
using Services.GitHub;
using Services.LogStreaming;

namespace Services.Orchestration;

/// <summary>
///     Polls GitHub Actions workflow runs and streams workflow logs to AutoMate clients.
/// </summary>
internal sealed class GitHubWorkflowMonitor(
    IGitHubService gitHubService,
    ILogStreamer logStreamer,
    ILogger logger)
{
    /// <summary>
    ///     Number of workflow polling attempts before returning the latest observed run.
    /// </summary>
    private const int MaxWorkflowPollAttempts = 60;

    /// <summary>
    ///     Delay between workflow polling attempts.
    /// </summary>
    private static readonly TimeSpan WorkflowPollDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Polls GitHub until the matching workflow completes or polling attempts are exhausted.
    /// </summary>
    public async Task<GitHubWorkflowRunDto?> PollWorkflowRunAsync(CloudDeploymentRequestDto request, string commitSha,
        CancellationToken cancellationToken)
    {
        GitHubWorkflowRunDto? latestRun = null;
        string? lastStatusMessage = null;

        for (var attempt = 0; attempt < MaxWorkflowPollAttempts; attempt++)
        {
            var run = await gitHubService.GetLatestWorkflowRunAsync(request.GitHubAccessToken, request.RepositoryOwner,
                request.RepositoryName, request.WorkflowFileName, request.BranchName, commitSha, cancellationToken);

            if (run == null)
            {
                await Task.Delay(WorkflowPollDelay, cancellationToken);
                continue;
            }

            latestRun = run;
            logger.LogInformation(
                "[CloudDeploymentOrchestrator] GitHub workflow run {RunId} for {Owner}/{Repo}@{Branch}: {Status}/{Conclusion}. {Url}",
                run.Id, request.RepositoryOwner, request.RepositoryName, request.BranchName, run.Status,
                run.Conclusion ?? "pending", run.HtmlUrl);

            var statusMessage = $"{run.Status}/{run.Conclusion ?? "pending"}";
            if (!string.Equals(statusMessage, lastStatusMessage, StringComparison.OrdinalIgnoreCase))
            {
                await StreamBuildLogAsync(request.Config.ProjectId,
                    $"GitHub Actions run {run.Id}: {statusMessage}. {run.HtmlUrl}");
                lastStatusMessage = statusMessage;
            }

            if (string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase))
                return run;

            await Task.Delay(WorkflowPollDelay, cancellationToken);
        }

        return latestRun;
    }

    /// <summary>
    ///     Downloads and streams GitHub Actions logs for a completed workflow run.
    /// </summary>
    public async Task StreamWorkflowLogsAsync(CloudDeploymentRequestDto request, long runId, Guid projectId,
        CancellationToken cancellationToken)
    {
        try
        {
            var logs = await gitHubService.DownloadWorkflowRunLogsAsync(request.GitHubAccessToken,
                request.RepositoryOwner, request.RepositoryName, runId, cancellationToken);

            if (!string.IsNullOrWhiteSpace(logs))
                await logStreamer.StreamBuildLogsAsync(projectId, logs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "[CloudDeploymentOrchestrator] Failed to download GitHub Actions logs for run {RunId}.", runId);
        }
    }

    /// <summary>
    ///     Streams a prefixed cloud deployment build log line.
    /// </summary>
    public async Task StreamBuildLogAsync(Guid projectId, string message)
    {
        await logStreamer.StreamBuildLogsAsync(projectId, $"[cloud] {message}\r\n");
    }
}