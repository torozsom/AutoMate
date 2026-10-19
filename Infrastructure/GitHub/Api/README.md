# GitHub API operations

Authenticated repository/workflow operations, rate-limit classification and repository metadata caching.

## Contents

| File                                                       | Types or asset             | Responsibility                                                                                |
|------------------------------------------------------------|----------------------------|-----------------------------------------------------------------------------------------------|
| [GitHubApiRequestFactory.cs](GitHubApiRequestFactory.cs)   | `GitHubApiRequestFactory`  | Creates authenticated GitHub REST API requests with consistent headers and URI escaping.      |
| [GitHubRateLimitException.cs](GitHubRateLimitException.cs) | `GitHubRateLimitException` | GitHub asked this installation to pause API requests until the indicated time.                |
| [GitHubRepositoryCache.cs](GitHubRepositoryCache.cs)       | `GitHubRepositoryCache`    | Handles distributed cache reads and writes for authenticated GitHub repository lists.         |
| [GitHubService.cs](GitHubService.cs)                       | `GitHubService`            | Coordinates GitHub repository, secret, workflow, and log operations for AutoMate deployments. |

## Usage and integration

GitHubService implements IGitHubService for listing repos, commit recovery/publication, repository secrets, dispatch,
progress and completed logs. GitHubApiRequestFactory centralizes headers/escaping. GitHubRepositoryCache wraps
distributed metadata reads/writes; GitHubRateLimitException communicates the provider's retry time.

## Contracts and lifecycle

Cache keys hash the access token and use ten-minute TTL; cache failures fall back to the API without storing
tokens/logs/context. Treat private repository DTOs as operational data, not public telemetry dimensions.

Correlate workflows by actual branch/commit/attempt and handle throttling separately from permanent failure. Signed
log-download redirects must not receive OAuth headers. Cancellation reaches HTTP/archive reads. Log finite
status/failure types, not repository names, secret names, URLs or provider bodies.

## Related documentation

- [Parent module](../README.md)
- [Port](../../../Application/Abstractions/GitHub/README.md)
- [Projection/log helpers](../Workflows/README.md)
- [HTTP/log tests](../../../Test/Infra/GitHub/GitHubServiceWorkflowLogTests.cs)
- [Commit recovery tests](../../../Test/Infra/GitHub/GitHubSaasCommitRecoveryTests.cs)
