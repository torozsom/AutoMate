# Repository import pages

GitHub repository listing/import and capability-gated local source discovery.

## Contents

| File                                               | Types or asset    | Responsibility                                                                                                                                                                                                                                               |
|----------------------------------------------------|-------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| [GitHubRepos.razor](GitHubRepos.razor)             | `GitHubRepos`     | Presents supported repositories from the authenticated owner GitHub connection.                                                                                                                                                                              |
| [GitHubRepos.razor.cs](GitHubRepos.razor.cs)       | `GitHubRepos`     | The GitHubRepos component is responsible for retrieving and displaying a list of GitHub repositories associated with the authenticated user. It checks the user's authentication state and integrates with the user service to fetch GitHub tokens securely. |
| [GitHubRepos.razor.css](GitHubRepos.razor.css)     | Scoped stylesheet | Scoped styling for GitHubRepos.                                                                                                                                                                                                                              |
| [LocalGitRepos.razor](LocalGitRepos.razor)         | `LocalGitRepos`   | Presents local source discovery and saving when the hosting profile permits local projects.                                                                                                                                                                  |
| [LocalGitRepos.razor.cs](LocalGitRepos.razor.cs)   | `LocalGitRepos`   | The LocalGitRepos component is responsible for displaying a list of local Git projects that have been scanned and saved to the user's account. It also allows the user to search for and save new projects.                                                  |
| [LocalGitRepos.razor.css](LocalGitRepos.razor.css) | Scoped stylesheet | Scoped styling for LocalGitRepos.                                                                                                                                                                                                                            |

## Usage and integration

GitHubRepos maps /github-repos, resolves the connected account and lists supported repositories through IGitHubService.
LocalGitRepos maps /local-repos, scans a chosen root through ILocalSystemScannerService and saves selected .csproj
entries. Both use scoped application operations for account/project persistence.

## Contracts and lifecycle

Scanning/import describes source metadata, not a build/deployment success. Local source UI respects host capability;
server workflow remains authoritative. Per-row saves can overlap without sharing an EF scope. Cancel scans/reads on
disposal and preserve per-row submitting/status state. Paths, repository metadata and access tokens are private
operational data, not safe telemetry labels.

## Related documentation

- [Parent module](../README.md)
- [Discovery port](../../../../Application/Abstractions/Scanning/README.md)
- [Repository API](../../../../Application/Abstractions/GitHub/README.md)
- [Save operations](../../../../Application/Data/Apps/README.md)
- [Scopes](../../../Services/ScopedApplicationOperations.cs)
- [Operation isolation](../../../../Test/Web/Hosting/ScopedApplicationOperationsTests.cs)
