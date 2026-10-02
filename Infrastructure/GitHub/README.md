# GitHub

GitHub API infrastructure adapter implementation.

GitHub workflow, job, and step state are normalized into deployment diagnostics while the run is active. Completed job
logs are fetched through GitHub's supported job-log endpoint, normalized and redacted before terminal delivery, and
deduplicated across restarts with non-sensitive persisted checkpoints. The final run archive is used only to reconcile
jobs whose individual log was unavailable.

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
