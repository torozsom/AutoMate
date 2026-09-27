# GitHub

GitHub API infrastructure adapter implementation.

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
