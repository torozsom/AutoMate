using System.Security.Cryptography;
using System.Text;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.GitHub;
using Domain.DTO;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.GitHub;

namespace Application.Orchestration;

/// <summary>Polls GitHub Actions workflow, job, and step state and publishes safe incremental diagnostics.</summary>
internal sealed class GitHubWorkflowMonitor(
    AutoMateDbContext dbContext,
    IGitHubService gitHubService,
    IDeploymentDiagnosticPublisher diagnostics,
    IDiagnosticRedactor redactor,
    GitHubWorkflowMonitoringOptions options)
{
    private readonly int _maxWorkflowPollAttempts = Math.Max(1,
        options.MaximumMonitoringMinutes * 60 / Math.Max(1, options.PollIntervalSeconds));
    private readonly TimeSpan _workflowPollDelay = TimeSpan.FromSeconds(Math.Clamp(options.PollIntervalSeconds, 1, 60));
    private readonly GitHubWorkflowCheckpointStore _checkpoints = new(dbContext);

    /// <summary>Polls the matched run until terminal state, publishing state and job logs as GitHub exposes them.</summary>
    public async Task<GitHubWorkflowRunDto?> PollWorkflowRunAsync(CloudDeploymentRequestDto request,
        Deployment deployment, string commitSha, CancellationToken cancellationToken)
    {
        GitHubWorkflowRunDto? latestRun = null;

        for (var attempt = 0; attempt < _maxWorkflowPollAttempts; attempt++)
        {
            var run = await gitHubService.GetLatestWorkflowRunAsync(request.GitHubAccessToken, request.RepositoryOwner,
                request.RepositoryName, request.WorkflowFileName, request.BranchName, commitSha, cancellationToken);
            if (run is null)
            {
                await Task.Delay(_workflowPollDelay, cancellationToken);
                continue;
            }

            latestRun = run;
            if (deployment.CloudGitHubActionRunId != run.Id)
            {
                deployment.CloudGitHubActionRunId = run.Id;
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await StreamRunAsync(request, deployment, run, cancellationToken);
            if (string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                await ReconcileFinalLogsAsync(request, deployment, run, cancellationToken);
                return run;
            }

            await Task.Delay(_workflowPollDelay, cancellationToken);
        }

        await PublishAsync(deployment.Id, request.Config.ProjectId, "GitHub workflow monitoring timed out after 60 minutes.\r\n",
            DeploymentDiagnosticKind.Annotation, DeploymentDiagnosticSeverity.Warning, null, cancellationToken);
        return latestRun;
    }

    /// <summary>Streams one cloud deployment preparation line through the diagnostic pipeline.</summary>
    public ValueTask StreamBuildLogAsync(Guid projectId, string message) => PublishAsync(null, projectId,
        $"[cloud] {message}\r\n", DeploymentDiagnosticKind.BuildProgress, DeploymentDiagnosticSeverity.Information,
        null, CancellationToken.None);

    private async Task StreamRunAsync(CloudDeploymentRequestDto request, Deployment deployment,
        GitHubWorkflowRunDto run, CancellationToken cancellationToken)
    {
        var workflow = await _checkpoints.GetOrCreateWorkflowAsync(deployment.Id, run.Id, run.Attempt,
            cancellationToken);
        var workflowFingerprint = Hash($"{run.Status}|{run.Conclusion}");
        if (!string.Equals(workflow.LastWorkflowStateFingerprint, workflowFingerprint, StringComparison.Ordinal))
        {
            workflow.LastWorkflowStateFingerprint = workflowFingerprint;
            await PublishAsync(deployment.Id, request.Config.ProjectId,
                $"GitHub Actions run {run.Id}: {run.Status}/{run.Conclusion ?? "pending"}. {run.HtmlUrl}\r\n",
                DeploymentDiagnosticKind.WorkflowState, SeverityFor(run.Conclusion), new Dictionary<string, string>
                {
                    ["workflow.run_id"] = run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["workflow.attempt"] = run.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["workflow.status"] = run.Status,
                    ["workflow.conclusion"] = run.Conclusion ?? "pending"
                }, cancellationToken);
            await _checkpoints.SaveAsync(cancellationToken);
        }

        var jobs = await gitHubService.GetWorkflowJobsAsync(request.GitHubAccessToken, request.RepositoryOwner,
            request.RepositoryName, run.Id, cancellationToken);
        foreach (var job in jobs)
            await StreamJobAsync(request, deployment, workflow, run, job, cancellationToken);
    }

    private async Task StreamJobAsync(CloudDeploymentRequestDto request, Deployment deployment,
        GitHubWorkflowCheckpoint workflow, GitHubWorkflowRunDto run, GitHubWorkflowJobDto job,
        CancellationToken cancellationToken)
    {
        var checkpoint = await _checkpoints.GetOrCreateJobAsync(workflow, job.Id, job.Name, cancellationToken);
        var stateFingerprint = Hash($"{job.Status}|{job.Conclusion}|{string.Join('|', job.Steps.Select(step =>
            $"{step.Number}:{step.Status}:{step.Conclusion}"))}");
        if (!string.Equals(checkpoint.LastStateFingerprint, stateFingerprint, StringComparison.Ordinal))
        {
            checkpoint.LastStateFingerprint = stateFingerprint;
            await PublishAsync(deployment.Id, request.Config.ProjectId,
                $"GitHub Actions job {job.Name}: {job.Status}/{job.Conclusion ?? "pending"}. {job.HtmlUrl}\r\n",
                DeploymentDiagnosticKind.JobState, SeverityFor(job.Conclusion), JobAttributes(run, job), cancellationToken);

            foreach (var step in job.Steps)
                await PublishAsync(deployment.Id, request.Config.ProjectId,
                    $"GitHub Actions step {job.Name} / {step.Name}: {step.Status}/{step.Conclusion ?? "pending"}.\r\n",
                    DeploymentDiagnosticKind.StepState, SeverityFor(step.Conclusion), new Dictionary<string, string>(JobAttributes(run, job))
                    {
                        ["workflow.step_number"] = step.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["workflow.step_name"] = step.Name,
                        ["workflow.step_status"] = step.Status,
                        ["workflow.step_conclusion"] = step.Conclusion ?? "pending"
                    }, cancellationToken);
            await _checkpoints.SaveAsync(cancellationToken);
        }

        if (job.Status is "in_progress" or "completed" && !checkpoint.IsLogFinal &&
            checkpoint.LogAvailability != GitHubWorkflowLogAvailability.AccessDenied)
            await StreamJobLogAsync(request, deployment, run, job, checkpoint, cancellationToken);
    }

    private async Task StreamJobLogAsync(CloudDeploymentRequestDto request, Deployment deployment,
        GitHubWorkflowRunDto run, GitHubWorkflowJobDto job, GitHubWorkflowJobCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var download = await gitHubService.DownloadWorkflowJobLogsAsync(request.GitHubAccessToken,
            request.RepositoryOwner, request.RepositoryName, job.Id, cancellationToken);
        checkpoint.LogAvailability = download.Availability;
        checkpoint.LastLogCheckedAt = DateTimeOffset.UtcNow;

        if (download.Availability != GitHubWorkflowLogAvailability.Available || string.IsNullOrEmpty(download.Content))
        {
            await PublishLogAvailabilityAsync(deployment.Id, request.Config.ProjectId, run, job, download.Availability,
                cancellationToken);
            await _checkpoints.SaveAsync(cancellationToken);
            return;
        }

        var attributes = JobAttributes(run, job);
        var redactedLines = GitHubWorkflowLogNormalizer.Normalize(download.Content)
            .Select(line => redactor.Redact(CreateEvent(deployment.Id, request.Config.ProjectId, line,
                DeploymentDiagnosticKind.Log, DeploymentDiagnosticSeverity.Information, attributes)).Event.Message)
            .ToArray();
        var currentHash = GitHubWorkflowLogNormalizer.Hash(redactedLines);
        var previousCount = checkpoint.LastLogLineCount;
        var prefixMatches = previousCount <= redactedLines.Length &&
                            string.Equals(checkpoint.LastLogPrefixHash,
                                GitHubWorkflowLogNormalizer.Hash(redactedLines.Take(previousCount)), StringComparison.Ordinal);

        if (previousCount > 0 && !prefixMatches)
        {
            await PublishAsync(deployment.Id, request.Config.ProjectId,
                $"GitHub Actions job {job.Name} log changed before its checkpoint; final reconciliation will be used.\r\n",
                DeploymentDiagnosticKind.Annotation, DeploymentDiagnosticSeverity.Warning, attributes, cancellationToken);
            await _checkpoints.SaveAsync(cancellationToken);
            return;
        }

        foreach (var line in redactedLines.Skip(previousCount))
            await diagnostics.PublishAsync(CreateEvent(deployment.Id, request.Config.ProjectId, line,
                DeploymentDiagnosticKind.Log, DeploymentDiagnosticSeverity.Information, attributes), cancellationToken);

        checkpoint.LastLogLineCount = redactedLines.Length;
        checkpoint.LastLogPrefixHash = currentHash;
        checkpoint.LastLogContentHash = currentHash;
        checkpoint.IsLogFinal = string.Equals(job.Status, "completed", StringComparison.OrdinalIgnoreCase);
        await _checkpoints.SaveAsync(cancellationToken);
    }

    private async Task ReconcileFinalLogsAsync(CloudDeploymentRequestDto request, Deployment deployment,
        GitHubWorkflowRunDto run, CancellationToken cancellationToken)
    {
        var workflow = await _checkpoints.GetOrCreateWorkflowAsync(deployment.Id, run.Id, run.Attempt,
            cancellationToken);
        if (workflow.FinalReconciledAt is not null) return;

        var incompleteJobs = workflow.JobCheckpoints.Where(item => !item.IsLogFinal).ToArray();
        if (incompleteJobs.Length == 0)
        {
            workflow.FinalReconciledAt = DateTimeOffset.UtcNow;
            await _checkpoints.SaveAsync(cancellationToken);
            return;
        }

        var archiveEntries = await gitHubService.DownloadWorkflowRunLogEntriesAsync(request.GitHubAccessToken,
            request.RepositoryOwner, request.RepositoryName, run.Id, cancellationToken);
        if (archiveEntries.Count == 0)
        {
            await PublishAsync(deployment.Id, request.Config.ProjectId,
                "GitHub Actions final log archive was unavailable; some job output could not be reconciled.\r\n",
                DeploymentDiagnosticKind.Annotation, DeploymentDiagnosticSeverity.Warning,
                new Dictionary<string, string> { ["workflow.run_id"] = run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                cancellationToken);
            return;
        }

        var consumedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var checkpoint in incompleteJobs)
        {
            var matchingEntries = archiveEntries.Where(entry => !consumedEntries.Contains(entry.Path) &&
                entry.Path.Contains(checkpoint.JobName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matchingEntries.Length == 0)
            {
                await PublishAsync(deployment.Id, request.Config.ProjectId,
                    $"GitHub Actions final archive did not contain a stable entry for job {checkpoint.JobName}.\r\n",
                    DeploymentDiagnosticKind.Annotation, DeploymentDiagnosticSeverity.Warning,
                    new Dictionary<string, string> { ["workflow.run_id"] = run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    cancellationToken);
                continue;
            }

            foreach (var entry in matchingEntries) consumedEntries.Add(entry.Path);
            var attributes = new Dictionary<string, string>
            {
                ["workflow.run_id"] = run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["workflow.job_id"] = checkpoint.JobId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["workflow.job_name"] = checkpoint.JobName,
                ["workflow.archive"] = "final"
            };
            var redactedLines = GitHubWorkflowLogNormalizer.Normalize(string.Join("\n", matchingEntries.Select(entry => entry.Content)))
                .Select(line => redactor.Redact(CreateEvent(deployment.Id, request.Config.ProjectId, line,
                    DeploymentDiagnosticKind.Log, DeploymentDiagnosticSeverity.Information, attributes)).Event.Message)
                .ToArray();
            var archiveHash = GitHubWorkflowLogNormalizer.Hash(redactedLines);
            var prefixMatches = checkpoint.LastLogLineCount == 0 || checkpoint.LastLogLineCount <= redactedLines.Length &&
                string.Equals(checkpoint.LastLogPrefixHash,
                    GitHubWorkflowLogNormalizer.Hash(redactedLines.Take(checkpoint.LastLogLineCount)),
                    StringComparison.Ordinal);
            if (!string.Equals(checkpoint.FinalArchiveContentHash, archiveHash, StringComparison.Ordinal) && prefixMatches)
                foreach (var line in redactedLines.Skip(checkpoint.LastLogLineCount))
                    await diagnostics.PublishAsync(CreateEvent(deployment.Id, request.Config.ProjectId, line,
                        DeploymentDiagnosticKind.Log, DeploymentDiagnosticSeverity.Information, attributes), cancellationToken);

            if (!prefixMatches)
                await PublishAsync(deployment.Id, request.Config.ProjectId,
                    $"GitHub Actions final archive for job {checkpoint.JobName} did not match the streamed checkpoint; duplicate output was suppressed.\r\n",
                    DeploymentDiagnosticKind.Annotation, DeploymentDiagnosticSeverity.Warning, attributes, cancellationToken);

            checkpoint.FinalArchiveContentHash = archiveHash;
            checkpoint.LastLogLineCount = redactedLines.Length;
            checkpoint.LastLogPrefixHash = archiveHash;
            checkpoint.LastLogContentHash = archiveHash;
            checkpoint.IsLogFinal = true;
        }
        workflow.FinalReconciledAt = DateTimeOffset.UtcNow;
        await _checkpoints.SaveAsync(cancellationToken);
    }

    private async Task PublishLogAvailabilityAsync(Guid deploymentId, Guid projectId, GitHubWorkflowRunDto run,
        GitHubWorkflowJobDto job, GitHubWorkflowLogAvailability availability, CancellationToken cancellationToken)
    {
        if (availability == GitHubWorkflowLogAvailability.NotAvailable) return;
        var severity = availability == GitHubWorkflowLogAvailability.AccessDenied
            ? DeploymentDiagnosticSeverity.Error
            : DeploymentDiagnosticSeverity.Warning;
        await PublishAsync(deploymentId, projectId, $"GitHub Actions job log for {job.Name} is {availability}.\r\n",
            DeploymentDiagnosticKind.Annotation, severity, JobAttributes(run, job), cancellationToken);
    }

    private static Dictionary<string, string> JobAttributes(GitHubWorkflowRunDto run, GitHubWorkflowJobDto job) => new()
    {
        ["workflow.run_id"] = run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["workflow.attempt"] = run.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["workflow.job_id"] = job.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["workflow.job_name"] = job.Name,
        ["workflow.job_status"] = job.Status,
        ["workflow.job_conclusion"] = job.Conclusion ?? "pending"
    };

    private ValueTask PublishAsync(Guid? deploymentId, Guid projectId, string message, DeploymentDiagnosticKind kind,
        DeploymentDiagnosticSeverity severity, IReadOnlyDictionary<string, string>? attributes,
        CancellationToken cancellationToken) => diagnostics.PublishAsync(CreateEvent(deploymentId, projectId, message,
        kind, severity, attributes), cancellationToken);

    private static DeploymentDiagnosticEvent CreateEvent(Guid? deploymentId, Guid projectId, string message,
        DeploymentDiagnosticKind kind, DeploymentDiagnosticSeverity severity, IReadOnlyDictionary<string, string>? attributes)
        => new(projectId, deploymentId, DeploymentDiagnosticSource.GitHubActions, kind, severity, DateTimeOffset.UtcNow,
            message, new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Build), attributes,
            SourceIdentity: CreateSourceIdentity(kind, attributes));

    private static DeploymentDiagnosticSourceIdentity CreateSourceIdentity(DeploymentDiagnosticKind kind,
        IReadOnlyDictionary<string, string>? attributes)
    {
        var component = kind switch
        {
            DeploymentDiagnosticKind.WorkflowState => DeploymentDiagnosticComponent.Workflow,
            DeploymentDiagnosticKind.JobState => DeploymentDiagnosticComponent.Job,
            DeploymentDiagnosticKind.StepState => DeploymentDiagnosticComponent.Step,
            _ => DeploymentDiagnosticComponent.Job
        };
        var instanceId = attributes?.GetValueOrDefault("workflow.job_id") ??
                         attributes?.GetValueOrDefault("workflow.run_id");
        return new DeploymentDiagnosticSourceIdentity(component,
            kind is DeploymentDiagnosticKind.Annotation ? DeploymentDiagnosticStream.System : DeploymentDiagnosticStream.StandardOutput,
            instanceId);
    }

    private static DeploymentDiagnosticSeverity SeverityFor(string? conclusion) => conclusion?.ToLowerInvariant() switch
    {
        "failure" or "timed_out" or "cancelled" => DeploymentDiagnosticSeverity.Error,
        "skipped" or "neutral" => DeploymentDiagnosticSeverity.Warning,
        _ => DeploymentDiagnosticSeverity.Information
    };

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
