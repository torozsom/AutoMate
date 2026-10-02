using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.GitHub;
using Domain.DTO;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.GitHub;
using Microsoft.Extensions.Logging;

namespace Application.Orchestration;

/// <summary>Polls GitHub Actions workflow, job, and step state and publishes safe incremental diagnostics.</summary>
internal sealed class GitHubWorkflowMonitor(
    AutoMateDbContext dbContext,
    IGitHubService gitHubService,
    IDeploymentDiagnosticPublisher diagnostics,
    IDiagnosticRedactor redactor,
    GitHubWorkflowMonitoringOptions options,
    ILogger<GitHubWorkflowMonitor> logger)
{
    /// <summary>Persists workflow and job cursors used to avoid duplicate terminal output.</summary>
    private readonly GitHubWorkflowCheckpointStore _checkpoints = new(dbContext);

    /// <summary>Bounds the time spent waiting for a GitHub run to reach a terminal state.</summary>
    private readonly int _maxWorkflowPollAttempts = Math.Max(1,
        options.MaximumMonitoringMinutes * 60 / Math.Max(1, options.PollIntervalSeconds));

    /// <summary>Suppresses repeated job-state lines when only a step changes.</summary>
    private readonly Dictionary<long, string> _observedJobStates = new();

    /// <summary>Suppresses repeated step-state lines within this monitor instance.</summary>
    private readonly Dictionary<(long JobId, int StepNumber), string> _observedStepStates = new();

    /// <summary>Interval between GitHub workflow and job status requests.</summary>
    private readonly TimeSpan _workflowPollDelay = TimeSpan.FromSeconds(Math.Clamp(options.PollIntervalSeconds, 1, 60));

    /// <summary>Polls the matched run until terminal state, publishing progress before completed-run logs.</summary>
    public async Task<GitHubWorkflowRunDto?> PollWorkflowRunAsync(CloudDeploymentRequestDto request,
        Deployment deployment, string commitSha, CancellationToken cancellationToken)
    {
        GitHubWorkflowRunDto? latestRun = null;

        for (var attempt = 0; attempt < _maxWorkflowPollAttempts; attempt++)
        {
            GitHubWorkflowRunDto? run;
            try
            {
                run = await gitHubService.GetLatestWorkflowRunAsync(request.GitHubAccessToken,
                    request.RepositoryOwner, request.RepositoryName, request.WorkflowFileName, request.BranchName,
                    commitSha, cancellationToken);
            }
            catch (HttpRequestException)
            {
                await PublishAsync(deployment.Id, request.Config.ProjectId,
                    "GitHub Actions workflow polling failed; AutoMate will retry on the next poll.\r\n",
                    DeploymentDiagnosticKind.Annotation, DeploymentDiagnosticSeverity.Warning, null, cancellationToken);
                await Task.Delay(_workflowPollDelay, cancellationToken);
                continue;
            }

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

            try
            {
                await StreamRunAsync(request, deployment, run, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    "GitHub workflow diagnostic collection failed for run {RunId} ({FailureType}); workflow result polling continues.",
                    run.Id, ex.GetType().Name);
            }

            if (string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await ReconcileFinalLogsAsync(request, deployment, run, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(
                        "GitHub final log reconciliation failed for run {RunId} ({FailureType}); preserving the workflow conclusion.",
                        run.Id, ex.GetType().Name);
                }

                return run;
            }

            await Task.Delay(_workflowPollDelay, cancellationToken);
        }

        await PublishAsync(deployment.Id, request.Config.ProjectId,
            "GitHub workflow monitoring timed out after 60 minutes.\r\n",
            DeploymentDiagnosticKind.Annotation, DeploymentDiagnosticSeverity.Warning, null, cancellationToken);
        return latestRun;
    }

    /// <summary>Streams one cloud deployment preparation line through the diagnostic pipeline.</summary>
    public async ValueTask StreamBuildLogAsync(Guid projectId, string message)
    {
        try
        {
            await PublishAsync(null, projectId,
                $"[cloud] {message}\r\n", DeploymentDiagnosticKind.BuildProgress,
                DeploymentDiagnosticSeverity.Information, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Cloud deployment diagnostic delivery failed for project {ProjectId} ({FailureType}).",
                projectId, ex.GetType().Name);
        }
    }

    /// <summary>Publishes changed run and job states, then fetches job text only for a completed run.</summary>
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
                    ["workflow.run_id"] = run.Id.ToString(CultureInfo.InvariantCulture),
                    ["workflow.attempt"] = run.Attempt.ToString(CultureInfo.InvariantCulture),
                    ["workflow.status"] = run.Status,
                    ["workflow.conclusion"] = run.Conclusion ?? "pending"
                }, cancellationToken);
            await _checkpoints.SaveAsync(cancellationToken);
        }

        IReadOnlyList<GitHubWorkflowJobDto> jobs;
        try
        {
            jobs = await gitHubService.GetWorkflowJobsAsync(request.GitHubAccessToken, request.RepositoryOwner,
                request.RepositoryName, run.Id, cancellationToken);
        }
        catch (HttpRequestException)
        {
            await PublishAsync(deployment.Id, request.Config.ProjectId,
                "GitHub Actions job discovery failed; AutoMate will retry on the next poll.\r\n",
                DeploymentDiagnosticKind.Annotation, DeploymentDiagnosticSeverity.Warning,
                new Dictionary<string, string> { ["workflow.run_id"] = run.Id.ToString(CultureInfo.InvariantCulture) },
                cancellationToken);
            return;
        }

        if (jobs.Count == 0 && string.Equals(run.Status, "in_progress", StringComparison.OrdinalIgnoreCase))
            await PublishAsync(deployment.Id, request.Config.ProjectId,
                "GitHub Actions has not reported a job yet; AutoMate is continuing to poll this run.\r\n",
                DeploymentDiagnosticKind.WorkflowState, DeploymentDiagnosticSeverity.Information,
                new Dictionary<string, string> { ["workflow.run_id"] = run.Id.ToString(CultureInfo.InvariantCulture) },
                cancellationToken);

        foreach (var job in jobs)
            await StreamJobAsync(request, deployment, workflow, run, job, cancellationToken);
    }

    /// <summary>Publishes changed job/step states and defers log text until workflow completion.</summary>
    private async Task StreamJobAsync(CloudDeploymentRequestDto request, Deployment deployment,
        GitHubWorkflowCheckpoint workflow, GitHubWorkflowRunDto run, GitHubWorkflowJobDto job,
        CancellationToken cancellationToken)
    {
        var checkpoint = await _checkpoints.GetOrCreateJobAsync(workflow, job.Id, job.Name, cancellationToken);
        var stateFingerprint = Hash($"{job.Status}|{job.Conclusion}|{string.Join('|', job.Steps.Select(step =>
            $"{step.Number}:{step.Status}:{step.Conclusion}"))}");
        if (!string.Equals(checkpoint.LastStateFingerprint, stateFingerprint, StringComparison.Ordinal))
        {
            var jobState = $"{job.Status}|{job.Conclusion}";
            if (!_observedJobStates.TryGetValue(job.Id, out var previousJobState) || previousJobState != jobState)
            {
                await PublishAsync(deployment.Id, request.Config.ProjectId,
                    $"GitHub Actions job {job.Name}: {job.Status}/{job.Conclusion ?? "pending"}. {job.HtmlUrl}\r\n",
                    DeploymentDiagnosticKind.JobState, SeverityFor(job.Conclusion), JobAttributes(run, job),
                    cancellationToken);
                _observedJobStates[job.Id] = jobState;
            }

            foreach (var step in job.Steps)
            {
                if (string.Equals(step.Status, "pending", StringComparison.OrdinalIgnoreCase)) continue;
                var stepKey = (job.Id, step.Number);
                var stepState = $"{step.Status}|{step.Conclusion}";
                if (_observedStepStates.TryGetValue(stepKey, out var previousStepState) &&
                    previousStepState == stepState) continue;

                await PublishAsync(deployment.Id, request.Config.ProjectId,
                    $"GitHub Actions step {job.Name} / {step.Name}: {step.Status}/{step.Conclusion ?? "pending"}.\r\n",
                    DeploymentDiagnosticKind.StepState, SeverityFor(step.Conclusion),
                    new Dictionary<string, string>(JobAttributes(run, job))
                    {
                        ["workflow.step_number"] = step.Number.ToString(CultureInfo.InvariantCulture),
                        ["workflow.step_name"] = step.Name,
                        ["workflow.step_status"] = step.Status,
                        ["workflow.step_conclusion"] = step.Conclusion ?? "pending"
                    }, cancellationToken);
                _observedStepStates[stepKey] = stepState;
            }

            checkpoint.LastStateFingerprint = stateFingerprint;
            await _checkpoints.SaveAsync(cancellationToken);
        }

        // Status is live; text is intentionally deferred until every workflow step has finished.
        if (string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(job.Status, "completed", StringComparison.OrdinalIgnoreCase) && !checkpoint.IsLogFinal &&
            checkpoint.LogAvailability != GitHubWorkflowLogAvailability.AccessDenied)
            await StreamJobLogAsync(request, deployment, run, job, checkpoint, cancellationToken);
    }

    /// <summary>Publishes a bounded, redacted segment of a completed job log using its durable line checkpoint.</summary>
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
        var previousCount = checkpoint.LastLogLineCount;

        // Hash the redacted representation so checkpoints never depend on secret-bearing source text.
        string RedactLine(string line)
        {
            return redactor.Redact(CreateEvent(deployment.Id, request.Config.ProjectId, line,
                DeploymentDiagnosticKind.Log, DeploymentDiagnosticSeverity.Information, attributes)).Event.Message;
        }

        var (prefixHash, prefixCount) = GitHubWorkflowLogNormalizer.HashWithCount(
            GitHubWorkflowLogNormalizer.EnumerateLines(download.Content).Take(previousCount).Select(RedactLine));
        var prefixMatches = prefixCount == previousCount &&
                            (previousCount == 0 || string.Equals(checkpoint.LastLogPrefixHash, prefixHash,
                                StringComparison.Ordinal));

        if (previousCount > 0 && !prefixMatches)
        {
            await PublishAsync(deployment.Id, request.Config.ProjectId,
                $"GitHub Actions job {job.Name} log changed before its checkpoint; final reconciliation will be used.\r\n",
                DeploymentDiagnosticKind.Annotation, DeploymentDiagnosticSeverity.Warning, attributes,
                cancellationToken);
            await _checkpoints.SaveAsync(cancellationToken);
            return;
        }

        var chunk = GitHubWorkflowLogNormalizer.ReadChunk(download.Content, previousCount);
        var redactedLines = chunk.Lines.Select(RedactLine).ToArray();
        foreach (var line in redactedLines)
            await diagnostics.PublishAsync(CreateEvent(deployment.Id, request.Config.ProjectId, line,
                DeploymentDiagnosticKind.Log, DeploymentDiagnosticSeverity.Information, attributes), cancellationToken);

        checkpoint.LastLogLineCount = previousCount + redactedLines.Length;
        var currentHash = GitHubWorkflowLogNormalizer.Hash(
            GitHubWorkflowLogNormalizer.EnumerateLines(download.Content)
                .Take(checkpoint.LastLogLineCount).Select(RedactLine));
        checkpoint.LastLogPrefixHash = currentHash;
        checkpoint.LastLogContentHash = currentHash;
        checkpoint.IsLogFinal = !chunk.HasMore &&
                                string.Equals(job.Status, "completed", StringComparison.OrdinalIgnoreCase);
        await _checkpoints.SaveAsync(cancellationToken);
    }

    /// <summary>Fills missing post-run job output from the final archive without replaying checkpointed lines.</summary>
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
                new Dictionary<string, string> { ["workflow.run_id"] = run.Id.ToString(CultureInfo.InvariantCulture) },
                cancellationToken);
            return;
        }

        var consumedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var checkpoint in incompleteJobs)
        {
            var matchingEntries = archiveEntries.Where(entry => !consumedEntries.Contains(entry.Path) &&
                                                                entry.Path.Contains(checkpoint.JobName,
                                                                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matchingEntries.Length == 0)
            {
                await PublishAsync(deployment.Id, request.Config.ProjectId,
                    $"GitHub Actions final archive did not contain a stable entry for job {checkpoint.JobName}.\r\n",
                    DeploymentDiagnosticKind.Annotation, DeploymentDiagnosticSeverity.Warning,
                    new Dictionary<string, string>
                        { ["workflow.run_id"] = run.Id.ToString(CultureInfo.InvariantCulture) },
                    cancellationToken);
                continue;
            }

            foreach (var entry in matchingEntries) consumedEntries.Add(entry.Path);
            var attributes = new Dictionary<string, string>
            {
                ["workflow.run_id"] = run.Id.ToString(CultureInfo.InvariantCulture),
                ["workflow.job_id"] = checkpoint.JobId.ToString(CultureInfo.InvariantCulture),
                ["workflow.job_name"] = checkpoint.JobName,
                ["workflow.archive"] = "final"
            };
            var redactedLines = GitHubWorkflowLogNormalizer
                .EnumerateLines(string.Join("\n", matchingEntries.Select(entry => entry.Content)))
                .Select(line => redactor.Redact(CreateEvent(deployment.Id, request.Config.ProjectId, line,
                    DeploymentDiagnosticKind.Log, DeploymentDiagnosticSeverity.Information, attributes)).Event.Message);
            var archiveHash = GitHubWorkflowLogNormalizer.Hash(redactedLines);
            var archiveLineCount = redactedLines.Count();
            var prefixMatches = checkpoint.LastLogLineCount == 0 ||
                                (checkpoint.LastLogLineCount <= archiveLineCount &&
                                 string.Equals(checkpoint.LastLogPrefixHash,
                                     GitHubWorkflowLogNormalizer.Hash(redactedLines.Take(checkpoint.LastLogLineCount)),
                                     StringComparison.Ordinal));
            if (!string.Equals(checkpoint.FinalArchiveContentHash, archiveHash, StringComparison.Ordinal) &&
                prefixMatches)
                foreach (var line in redactedLines.Skip(checkpoint.LastLogLineCount))
                    await diagnostics.PublishAsync(CreateEvent(deployment.Id, request.Config.ProjectId, line,
                            DeploymentDiagnosticKind.Log, DeploymentDiagnosticSeverity.Information, attributes),
                        cancellationToken);

            if (!prefixMatches)
                await PublishAsync(deployment.Id, request.Config.ProjectId,
                    $"GitHub Actions final archive for job {checkpoint.JobName} did not match the streamed checkpoint; duplicate output was suppressed.\r\n",
                    DeploymentDiagnosticKind.Annotation, DeploymentDiagnosticSeverity.Warning, attributes,
                    cancellationToken);

            checkpoint.FinalArchiveContentHash = archiveHash;
            checkpoint.LastLogLineCount = archiveLineCount;
            checkpoint.LastLogPrefixHash = archiveHash;
            checkpoint.LastLogContentHash = archiveHash;
            checkpoint.IsLogFinal = true;
        }

        workflow.FinalReconciledAt = DateTimeOffset.UtcNow;
        await _checkpoints.SaveAsync(cancellationToken);
    }

    /// <summary>Reports a job-log access failure without treating temporary unavailability as an error.</summary>
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

    /// <summary>Builds non-secret workflow and job attributes for diagnostic correlation.</summary>
    private static Dictionary<string, string> JobAttributes(GitHubWorkflowRunDto run, GitHubWorkflowJobDto job)
    {
        return new Dictionary<string, string>
        {
            ["workflow.run_id"] = run.Id.ToString(CultureInfo.InvariantCulture),
            ["workflow.attempt"] = run.Attempt.ToString(CultureInfo.InvariantCulture),
            ["workflow.job_id"] = job.Id.ToString(CultureInfo.InvariantCulture),
            ["workflow.job_name"] = job.Name,
            ["workflow.job_status"] = job.Status,
            ["workflow.job_conclusion"] = job.Conclusion ?? "pending"
        };
    }

    /// <summary>Creates and publishes a typed GitHub Actions diagnostic.</summary>
    private ValueTask PublishAsync(Guid? deploymentId, Guid projectId, string message, DeploymentDiagnosticKind kind,
        DeploymentDiagnosticSeverity severity, IReadOnlyDictionary<string, string>? attributes,
        CancellationToken cancellationToken)
    {
        return diagnostics.PublishAsync(CreateEvent(deploymentId, projectId, message,
            kind, severity, attributes), cancellationToken);
    }

    /// <summary>Routes a GitHub Actions observation to the project build terminal with its source identity.</summary>
    private static DeploymentDiagnosticEvent CreateEvent(Guid? deploymentId, Guid projectId, string message,
        DeploymentDiagnosticKind kind, DeploymentDiagnosticSeverity severity,
        IReadOnlyDictionary<string, string>? attributes)
    {
        return new DeploymentDiagnosticEvent(projectId, deploymentId, DeploymentDiagnosticSource.GitHubActions, kind,
            severity,
            DateTimeOffset.UtcNow,
            message, new DeploymentTerminalChannel(DeploymentTerminalChannelKind.Build), attributes,
            SourceIdentity: CreateSourceIdentity(kind, attributes));
    }

    /// <summary>Maps workflow, job, and step diagnostics to stable component identities.</summary>
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
            kind is DeploymentDiagnosticKind.Annotation
                ? DeploymentDiagnosticStream.System
                : DeploymentDiagnosticStream.StandardOutput,
            instanceId);
    }

    /// <summary>Maps GitHub conclusions to terminal diagnostic severity.</summary>
    private static DeploymentDiagnosticSeverity SeverityFor(string? conclusion)
    {
        return conclusion?.ToLowerInvariant() switch
        {
            "failure" or "timed_out" or "cancelled" => DeploymentDiagnosticSeverity.Error,
            "skipped" or "neutral" => DeploymentDiagnosticSeverity.Warning,
            _ => DeploymentDiagnosticSeverity.Information
        };
    }

    /// <summary>Fingerprints state snapshots without persisting their display text.</summary>
    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}