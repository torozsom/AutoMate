# GitHub

GitHub API infrastructure adapter implementation.

GitHub workflow state and completed-run archive output are normalized into deployment diagnostics before terminal
delivery. Incremental job-log checkpoints remain a subsequent ingestion milestone.

## Source inventory

- `GitHubApiRequestFactory.cs`
- `GitHubRepositoryCache.cs`
- `GitHubRepositorySecretModels.cs`
- `GitHubSecretEncryptor.cs`
- `GitHubService.cs`
- `GitHubWorkflowLogReader.cs`
- `GitHubWorkflowRunMapper.cs`
- `GitHubWorkflowRunModels.cs`

## Boundary

Infrastructure may reference Application and Domain, but never Web. It implements ports and owns provider-specific behavior.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)
