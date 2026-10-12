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

- `Api/GitHubApiRequestFactory.cs`
- `Api/GitHubRepositoryCache.cs`
- `Secrets/GitHubRepositorySecretModels.cs`
- `Secrets/GitHubSecretEncryptor.cs`
- `Api/GitHubService.cs`
- `Workflows/GitHubWorkflowLogReader.cs`
- `Workflows/GitHubWorkflowLogNormalizer.cs`
- `Workflows/GitHubWorkflowJobMapper.cs`
- `Workflows/GitHubWorkflowJobModels.cs`
- `Workflows/GitHubWorkflowCheckpointStore.cs`
- `Workflows/GitHubWorkflowRunMapper.cs`
- `Workflows/GitHubWorkflowRunModels.cs`

## Boundary

Infrastructure may reference Application and Domain, but never Web. It implements ports and owns provider-specific
behavior.

## Related documentation

- [Solution navigation map](../../navigation.md)

Workflow/job queries and completed log downloads have correlated child spans. Tokens, repository/branch names, signed
URLs and output are excluded. Existing retry, checkpoint and progress-then-completed-logs behavior remains unchanged.

API/cache operational logs omit repository/branch/secret names and exception bodies. Cache failures retain API fallback;
cancellation and return contracts stay unchanged. Real SDK export regressions in
Web.Tests/Diagnostics/OperationalLoggingTests.cs
verify sensitive failure text never enters attributes or exception exports.

## Folder organization

Folders group related files while retaining the module's existing .NET namespaces and public contracts.

- `Api/` — `Api/GitHubApiRequestFactory.cs`, `Api/GitHubRateLimitException.cs`, `Api/GitHubRepositoryCache.cs`,
  `Api/GitHubService.cs`.
- `Authentication/` — `Authentication/GitHubAppCredentials.cs`, `Authentication/GitHubAppOptions.cs`.
- `Secrets/` — `Secrets/GitHubRepositorySecretModels.cs`, `Secrets/GitHubSecretEncryptor.cs`.
- `Webhooks/` — `Webhooks/GitHubWebhookReceiver.cs`.
- `Workflows/` — `Workflows/GitHubWorkflowCheckpointStore.cs`, `Workflows/GitHubWorkflowJobMapper.cs`,
  `Workflows/GitHubWorkflowJobModels.cs`, `Workflows/GitHubWorkflowLogNormalizer.cs`,
  `Workflows/GitHubWorkflowLogReader.cs`, `Workflows/GitHubWorkflowRunMapper.cs`,
  `Workflows/GitHubWorkflowRunModels.cs`.
