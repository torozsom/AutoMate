# GitHub

GitHub API infrastructure adapter implementation.
The SaaS GitHub App adapter validates the initiating user's repository write permission, resolves the repository
installation, and mints short-lived installation tokens. Signed `workflow_run` deliveries are deduplicated by GitHub
delivery ID and stored as minimal metadata. Provider throttling pauses launches for an installation.

GitHub workflow, job, and step state are normalized into deployment diagnostics while the run is active. Jobs are
discovered page by page, and each job/step status change is shown as GitHub reports it. Text output is intentionally
deferred until the entire workflow has completed: job-log downloads and the final run archive then supply normalized,
redacted output through bounded chunks and persisted deduplication checkpoints. Each log download follows GitHub's
short-lived signed redirect without forwarding the OAuth token to the download host.

Job/step state lines are emitted only when that state changes; pending steps are not printed. No per-step log
availability probe or runner-side forwarder is needed for the chosen progress-then-logs presentation. Diagnostic
delivery failures are isolated from the workflow conclusion.
Workflow poll/observation activities and source-only counters report polling failures, recovery and checkpointed
prefix suppression. These changes retain the progress-then-completed-logs ordering.

## Source inventory

- `GitHubApiRequestFactory.cs`
- `GitHubRepositoryCache.cs`
- `GitHubRepositorySecretModels.cs`
- `GitHubSecretEncryptor.cs`
- `GitHubService.cs`
- `GitHubWorkflowLogReader.cs`
- `GitHubWorkflowLogNormalizer.cs`
- `GitHubWorkflowJobMapper.cs`
- `GitHubWorkflowJobModels.cs`
- `GitHubWorkflowCheckpointStore.cs`
- `GitHubWorkflowRunMapper.cs`
- `GitHubWorkflowRunModels.cs`

## Boundary

Infrastructure may reference Application and Domain, but never Web. It implements ports and owns provider-specific
behavior.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)

Workflow/job queries and completed log downloads have correlated child spans. Tokens, repository/branch names, signed
URLs and output are excluded. Existing retry, checkpoint and progress-then-completed-logs behavior remains unchanged.

API/cache operational logs omit repository/branch/secret names and exception bodies. Cache failures retain API fallback;
cancellation and return contracts stay unchanged. Real SDK export regressions in Web.Tests/OperationalLoggingTests.cs
verify sensitive failure text never enters attributes or exception exports.
