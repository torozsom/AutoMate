using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Application.Abstractions.Diagnostics;
using Application.Abstractions.GitHub;
using Application.Diagnostics;
using Domain.DTO;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.GitHub;
using Microsoft.Extensions.Logging;

namespace Application.Orchestration;

/// <summary>
///     Polls GitHub Actions workflow, job, and step state and publishes safe incremental
///     diagnostics.
/// </summary>
/// <param name="dbContext">
///     Application metadata context used for ownership, leases and persisted state.
/// </param>
/// <param name="gitHubService">Provides the git hub service boundary used by this operation.</param>
/// <param name="diagnostics">Accepts normalized observations for redaction and durable delivery.</param>
/// <param name="redactor">Applies shared credential and unsafe-text masking.</param>
/// <param name="options">The current validated options used by this operation.</param>
/// <param name="logger">
///     Receives reviewed operational events without diagnostic payloads or credentials.
/// </param>
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

    /// <summary>
    ///     Collects one webhook-triggered snapshot without occupying a launch worker while waiting.
    /// </summary>
    /// <param name="request">
    ///     Validated operation request containing the selected resource scope and metadata.
    /// </param>
    /// <param name="deployment">Tracked deployment metadata used for this exact launch or history scope.</param>
    /// <param name="run">Verified cloud control record or provider workflow-run metadata.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    public async Task ObserveWorkflowRunAsync(CloudDeploymentRequestDto request, Deployment deployment,
        GitHubWorkflowRunDto run, CancellationToken cancellationToken)
    {
        await DeploymentTracing.RunAsync(DeploymentOperation.GitHubObserve, request.Config.ProjectId, deployment.Id,
            cancellationToken, async () =>
            {
                await StreamRunAsync(request, deployment, run, cancellationToken);
                if (string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase))
                    await ReconcileFinalLogsAsync(request, deployment, run, cancellationToken);
            });
    }

    /// <summary>
    ///     Polls the matched run until terminal state, publishing progress before completed-run
    ///     logs.
    /// </summary>
    /// <param name="request">
    ///     Validated operation request containing the selected resource scope and metadata.
    /// </param>
    /// <param name="deployment">Tracked deployment metadata used for this exact launch or history scope.</param>
    /// <param name="commitSha">Verified repository commit SHA.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>
    ///     A task returning the normalized verified workflow-run metadata, or null when no value is
    ///     available.
    /// </returns>
    public async Task<GitHubWorkflowRunDto?> PollWorkflowRunAsync(CloudDeploymentRequestDto request,
        Deployment deployment, string commitSha, CancellationToken cancellationToken)
    {
        using var activity = AutoMateTelemetry.Deployments.StartActivity("github.workflow.poll");
        activity?.SetTag("deployment.project.id", request.Config.ProjectId);
        activity?.SetTag("deployment.id", deployment.Id);
        GitHubWorkflowRunDto? latestRun = null;
        var recovering = false;

        for (var attempt = 0; attempt < _maxWorkflowPollAttempts; attempt++)
        {
            GitHubWorkflowRunDto? run;
            try
            {
                run = await DeploymentTracing.RunAsync(DeploymentOperation.GitHubPoll, request.Config.ProjectId,
                    deployment.Id,
                    cancellationToken, () => gitHubService.GetLatestWorkflowRunAsync(request.GitHubAccessToken,
                        request.RepositoryOwner, request.RepositoryName, request.WorkflowFileName, request.BranchName,
                        commitSha, cancellationToken));
            }
            catch (HttpRequestException)
            {
                recovering = true;
                AutoMateTelemetry.CollectorErrors.Add(1,
                    new KeyValuePair<string, object?>("deployment.source", "GitHubActions"));
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

            if (recovering)
            {
                AutoMateTelemetry.CollectorReconnects.Add(1,
                    new KeyValuePair<string, object?>("deployment.source", "GitHubActions"));
                recovering = false;
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
                AutoMateTelemetry.CollectorErrors.Add(1,
                    new KeyValuePair<string, object?>("deployment.source", "GitHubActions"));
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

                DeploymentTracing.Finish(activity, DeploymentTraceOutcome.Completed);
                return run;
            }

            await Task.Delay(_workflowPollDelay, cancellationToken);
        }

        await PublishAsync(deployment.Id, request.Config.ProjectId,
            "GitHub workflow monitoring timed out after 60 minutes.\r\n",
            DeploymentDiagnosticKind.Annotation, DeploymentDiagnosticSeverity.Warning, null, cancellationToken);
        DeploymentTracing.Finish(activity, DeploymentTraceOutcome.TimedOut);
        return latestRun;
    }

    /// <summary>
    ///     Streams one cloud deployment preparation line through the diagnostic pipeline.
    /// </summary>
    /// <param name="deploymentId">
    ///     The deployment identity; null is used only where legacy observations permit it.
    /// </param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="message">The message supplied to the operation.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    public async ValueTask StreamBuildLogAsync(Guid deploymentId, Guid projectId, string message)
    {
        try
        {
            await PublishAsync(deploymentId, projectId,
                $"[cloud] {message}\r\n", DeploymentDiagnosticKind.BuildProgress,
                DeploymentDiagnosticSeverity.Information, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Cloud deployment diagnostic delivery failed for project {ProjectId} ({FailureType}).",
                projectId, ex.GetType().Name);
        }
    }

    /// <summary>
    ///     Publishes changed run and job states, then fetches job text only for a completed run.
    /// </summary>
    /// <param name="request">
    ///     Validated operation request containing the selected resource scope and metadata.
    /// </param>
    /// <param name="deployment">Tracked deployment metadata used for this exact launch or history scope.</param>
    /// <param name="run">Verified cloud control record or provider workflow-run metadata.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
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
            jobs = await DeploymentTracing.RunAsync(DeploymentOperation.GitHubJobs, request.Config.ProjectId,
                deployment.Id,
                cancellationToken, () => gitHubService.GetWorkflowJobsAsync(request.GitHubAccessToken,
                    request.RepositoryOwner,
                    request.RepositoryName, run.Id, cancellationToken));
        }
        catch (HttpRequestException)
        {
            AutoMateTelemetry.CollectorErrors.Add(1,
                new KeyValuePair<string, object?>("deployment.source", "GitHubActions"));
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

    /// <summary>
    ///     Publishes changed job/step states and defers log text until workflow completion.
    /// </summary>
    /// <param name="request">
    ///     Validated operation request containing the selected resource scope and metadata.
    /// </param>
    /// <param name="deployment">Tracked deployment metadata used for this exact launch or history scope.</param>
    /// <param name="workflow">Verified workflow metadata or its durable checkpoint.</param>
    /// <param name="run">Verified cloud control record or provider workflow-run metadata.</param>
    /// <param name="job">The admitted deployment operation; its payload must not be logged.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
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

    /// <summary>
    ///     Publishes a bounded, redacted segment of a completed job log using its durable line
    ///     checkpoint.
    /// </summary>
    /// <param name="request">
    ///     Validated operation request containing the selected resource scope and metadata.
    /// </param>
    /// <param name="deployment">Tracked deployment metadata used for this exact launch or history scope.</param>
    /// <param name="run">Verified cloud control record or provider workflow-run metadata.</param>
    /// <param name="job">The admitted deployment operation; its payload must not be logged.</param>
    /// <param name="checkpoint">Durable source position used to avoid duplicate delivery.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private async Task StreamJobLogAsync(CloudDeploymentRequestDto request, Deployment deployment,
        GitHubWorkflowRunDto run, GitHubWorkflowJobDto job, GitHubWorkflowJobCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var download = await DeploymentTracing.RunAsync(DeploymentOperation.GitHubLogs, request.Config.ProjectId,
            deployment.Id,
            cancellationToken, () => gitHubService.DownloadWorkflowJobLogsAsync(request.GitHubAccessToken,
                request.RepositoryOwner, request.RepositoryName, job.Id, cancellationToken));
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
#pragma warning disable CS1587 // C# does not emit local-function XML documentation.
        /// <summary>
        ///     Masks a workflow line before it enters terminal or durable diagnostic delivery.
        /// </summary>
        /// <param name="line">One bounded source-output line to normalize and redact.</param>
        /// <returns>The bounded normalized text with supported unsafe values removed.</returns>
        string RedactLine(string line)
        {
            return redactor.Redact(CreateEvent(deployment.Id, request.Config.ProjectId, line,
                DeploymentDiagnosticKind.Log, DeploymentDiagnosticSeverity.Information, attributes)).Event.Message;
        }
#pragma warning restore CS1587

        var (prefixHash, prefixCount) = GitHubWorkflowLogNormalizer.HashWithCount(
            GitHubWorkflowLogNormalizer.EnumerateLines(download.Content).Take(previousCount).Select(RedactLine));
        var prefixMatches = prefixCount == previousCount &&
                            (previousCount == 0 || string.Equals(checkpoint.LastLogPrefixHash, prefixHash,
                                StringComparison.Ordinal));
        if (prefixMatches && previousCount > 0)
            AutoMateTelemetry.DiagnosticDuplicates.Add(previousCount,
                new KeyValuePair<string, object?>("deployment.source", "GitHubActions"));

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

    /// <summary>
    ///     Fills missing post-run job output from the final archive without replaying checkpointed
    ///     lines.
    /// </summary>
    /// <param name="request">
    ///     Validated operation request containing the selected resource scope and metadata.
    /// </param>
    /// <param name="deployment">Tracked deployment metadata used for this exact launch or history scope.</param>
    /// <param name="run">Verified cloud control record or provider workflow-run metadata.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
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

        var archiveEntries = await DeploymentTracing.RunAsync(DeploymentOperation.GitHubLogs, request.Config.ProjectId,
            deployment.Id,
            cancellationToken, () => gitHubService.DownloadWorkflowRunLogEntriesAsync(request.GitHubAccessToken,
                request.RepositoryOwner, request.RepositoryName, run.Id, cancellationToken));
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

    /// <summary>
    ///     Reports a job-log access failure without treating temporary unavailability as an error.
    /// </summary>
    /// <param name="deploymentId">
    ///     The deployment identity; null is used only where legacy observations permit it.
    /// </param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="run">Verified cloud control record or provider workflow-run metadata.</param>
    /// <param name="job">The admitted deployment operation; its payload must not be logged.</param>
    /// <param name="availability">
    ///     A safe availability or coverage notice; null indicates no reported notice.
    /// </param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
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

    /// <summary>
    ///     Builds non-secret workflow and job attributes for diagnostic correlation.
    /// </summary>
    /// <param name="run">Verified cloud control record or provider workflow-run metadata.</param>
    /// <param name="job">The admitted deployment operation; its payload must not be logged.</param>
    /// <returns>The job attributes selection with its documented bounds and ordering.</returns>
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

    /// <summary>
    ///     Creates and publishes a typed GitHub Actions diagnostic.
    /// </summary>
    /// <param name="deploymentId">
    ///     The deployment identity; null is used only where legacy observations permit it.
    /// </param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="message">The message supplied to the operation.</param>
    /// <param name="kind">Finite observation or assessment category used by this operation.</param>
    /// <param name="severity">The normalized severity used to rank a context group.</param>
    /// <param name="attributes">Optional bounded, non-secret diagnostic attributes.</param>
    /// <param name="cancellationToken">Cancels the underlying operation when observed.</param>
    /// <returns>A task representing completion of the asynchronous operation.</returns>
    private ValueTask PublishAsync(Guid? deploymentId, Guid projectId, string message, DeploymentDiagnosticKind kind,
        DeploymentDiagnosticSeverity severity, IReadOnlyDictionary<string, string>? attributes,
        CancellationToken cancellationToken)
    {
        return diagnostics.PublishAsync(CreateEvent(deploymentId, projectId, message,
            kind, severity, attributes), cancellationToken);
    }

    /// <summary>
    ///     Routes a GitHub Actions observation to the project build terminal with its source
    ///     identity.
    /// </summary>
    /// <param name="deploymentId">
    ///     The deployment identity; null is used only where legacy observations permit it.
    /// </param>
    /// <param name="projectId">The project identity used for authorization or correlation.</param>
    /// <param name="message">The message supplied to the operation.</param>
    /// <param name="kind">Finite observation or assessment category used by this operation.</param>
    /// <param name="severity">The normalized severity used to rank a context group.</param>
    /// <param name="attributes">Optional bounded, non-secret diagnostic attributes.</param>
    /// <returns>The normalized deployment-correlated observation.</returns>
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

    /// <summary>
    ///     Maps workflow, job, and step diagnostics to stable component identities.
    /// </summary>
    /// <param name="kind">Finite observation or assessment category used by this operation.</param>
    /// <param name="attributes">Optional bounded, non-secret diagnostic attributes.</param>
    /// <returns>The deployment diagnostic source identity projection derived from the verified inputs.</returns>
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

    /// <summary>
    ///     Maps GitHub conclusions to terminal diagnostic severity.
    /// </summary>
    /// <param name="conclusion">Provider terminal workflow outcome, if available.</param>
    /// <returns>The deployment diagnostic severity projection derived from the verified inputs.</returns>
    private static DeploymentDiagnosticSeverity SeverityFor(string? conclusion)
    {
        return conclusion?.ToLowerInvariant() switch
        {
            "failure" or "timed_out" or "cancelled" => DeploymentDiagnosticSeverity.Error,
            "skipped" or "neutral" => DeploymentDiagnosticSeverity.Warning,
            _ => DeploymentDiagnosticSeverity.Information
        };
    }

    /// <summary>
    ///     Fingerprints state snapshots without persisting their display text.
    /// </summary>
    /// <param name="value">The value to normalize, redact or validate.</param>
    /// <returns>The stable identity derived from the documented metadata or content.</returns>
    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}