# GitHub

`Services/GitHub` is AutoMate's GitHub integration module. It provides the
application-facing contract for repository browsing and the cloud deployment
operations that prepare, trigger, monitor, and report GitHub Actions
workflows.

The module is an external-service adapter. It does not own OAuth challenge
handling, authentication cookies, user persistence, deployment state
transitions, template generation, or UI rendering. Those responsibilities
remain in `Web`, `Services.Auth`, `Services.Data`, `Services.Orchestration`,
and `Services.Templating`.

## Responsibilities

- Retrieve the authenticated user's repositories from GitHub.
- Cache repository lists in the distributed cache without storing raw tokens
  in cache keys.
- Create or update a deployment branch and commit generated files.
- Encrypt and upsert GitHub Actions repository secrets.
- Dispatch workflows on a selected branch.
- Find the latest workflow run matching a branch, workflow, and optional
  commit SHA.
- Download workflow log archives and flatten them into streamable text.
- Apply consistent authentication, API version, media type, and URI escaping
  rules to REST requests.

## Module Structure

```text
Services/GitHub/
├── IGitHubService.cs
├── GitHubService.cs
├── GitHubApiRequestFactory.cs
├── GitHubRepositoryCache.cs
├── GitHubSecretEncryptor.cs
├── GitHubRepositorySecretModels.cs
├── GitHubWorkflowRunModels.cs
├── GitHubWorkflowRunMapper.cs
└── GitHubWorkflowLogReader.cs
```

Public GitHub API response contracts used outside this module live in
`Core/DTO`:

- `GitHubRepositoryDto`
- `GitHubWorkflowRunDto`

The internal records in this directory model only the GitHub JSON shapes
needed to perform a specific operation.

## Public Contract

`IGitHubService` exposes six operation groups:

| Operation | Purpose | Main consumer |
|---|---|---|
| `GetUserRepositoriesAsync` | List and cache repositories for the connected user. | `Web.Components.Pages.GitHubRepos` |
| `CommitCloudDeploymentFilesAsync` | Create/update a deployment branch and commit generated files. | `CloudDeploymentOrchestrator` |
| `UpsertRepositorySecretsAsync` | Encrypt and write Actions secrets. | `CloudDeploymentOrchestrator` |
| `DispatchWorkflowAsync` | Trigger a workflow dispatch. | Future/deployment workflow callers |
| `GetLatestWorkflowRunAsync` | Locate the relevant Actions run. | `GitHubWorkflowMonitor` |
| `DownloadWorkflowRunLogsAsync` | Download and flatten completed-run logs. | `GitHubWorkflowMonitor` |

The interface deliberately accepts access tokens at operation boundaries.
`Services.Data` retrieves the decrypted token for an authenticated remote
user, while the GitHub adapter uses it only for the outgoing request. Do not
persist tokens, place them in cache keys, or return them through UI models.

## Delivery Architecture

The module uses two GitHub client surfaces:

| Surface | Used for | Reason |
|---|---|---|
| Typed `HttpClient` | Repository listing, repository secrets, workflow dispatch, workflow runs, and workflow log download. | Explicit REST request construction and response DTO control. |
| Octokit `GitHubClient` | Repository lookup, branch references, Git trees, commits, and branch reference updates. | Git object and reference operations are provided directly by Octokit. |

`Web.Configs.ServiceConfiguration` registers the typed client with standard
HTTP resilience:

```csharp
services.AddHttpClient<IGitHubService, GitHubService>()
    .AddStandardResilienceHandler();
```

`GitHubService` sets the REST base address to
`https://api.github.com/` when the client has no base address and adds the
`AutoMate/1.0` product header required by GitHub.

## Authentication and Request Construction

`GitHubApiRequestFactory` creates authenticated REST requests with:

- `Authorization: Bearer <token>`;
- `Accept: application/vnd.github+json`;
- `X-GitHub-Api-Version: 2022-11-28`.

It escapes path segments individually while preserving the query string.
This is important for repository owners, names, workflow file names, branch
names, and secret names that may contain characters requiring URI escaping.
Do not concatenate new GitHub paths directly when the request factory can be
used.

The factory rejects empty access tokens immediately. Other public methods
validate required owner, repository, branch, workflow, and file inputs before
making external calls.

## Repository Browsing and Cache

`GetUserRepositoriesAsync` requests:

```text
GET /user/repos?sort=updated&per_page=100
```

The method:

1. returns an empty list for a missing access token;
2. checks the distributed cache unless `forceRefresh` is true;
3. calls GitHub when the cache misses;
4. deserializes the response into `GitHubRepositoryDto`;
5. caches the successful result for ten minutes;
6. returns the repository list to the caller.

The cache key is generated from a SHA-256 hash of the access token and encoded
as a URL-safe value:

```text
github_repos_<token-hash>
```

The raw token never appears in the key. Cache read/deserialization failures
fall back to GitHub rather than making repository browsing unavailable.
Cache write failures are logged and do not fail an otherwise successful API
operation. Cancellation is different: it is logged and rethrown by the cache
helper so the caller can stop promptly.

The Web repository page owns authenticated-user resolution and converts
unexpected failures into a user-facing status message. The GitHub module
returns an empty list for missing credentials, cancellation during the API
fetch, or an HTTP/network failure in repository browsing because that public
method is designed as a list operation rather than an exception-based
workflow.

## Cloud Deployment Git Operations

`CommitCloudDeploymentFilesAsync` commits generated deployment artifacts to a
branch:

1. validate access, repository, branch, commit message, and file inputs;
2. load the repository and its default branch;
3. load the default branch reference;
4. get the target branch or create it from the default branch SHA;
5. load the target commit and use its tree as the base;
6. add each `TemplateFile` as a regular `100644` blob;
7. create a tree and commit;
8. update the target branch reference;
9. return the created commit SHA.

Generated file paths are normalized from backslashes to forward slashes
before they are sent to GitHub. Empty file paths are rejected. The method
rethrows cancellation and other failures after logging; cloud orchestration
must treat a failed commit as a failed preparation step.

The target branch is created only when absent. Existing target branches are
updated from their current reference, so callers must provide a deliberate
branch name and understand that the operation advances that branch.

## Repository Secrets

`UpsertRepositorySecretsAsync` uses GitHub's repository public key flow:

1. request `actions/secrets/public-key`;
2. validate the returned key and key ID;
3. encrypt each secret value with `GitHubSecretEncryptor`;
4. send a `PUT` request for each secret;
5. require a successful response for every upsert.

`GitHubSecretEncryptor` uses Sodium sealed-box encryption with the Base64
public key returned by GitHub. The request contains only the encrypted value
and key ID; plaintext secret values must never be logged or included in
generated request diagnostics.

The method returns immediately for an empty secret dictionary. It validates
secret names and observes cancellation between individual upserts. A failed
public-key request or secret update propagates through `EnsureSuccessStatusCode`
and stops the deployment preparation workflow.

The cloud orchestrator supplies secrets generated from Azure OIDC setup and
container registry configuration. This module does not decide which secrets
are needed or how their values are obtained.

## Workflow Dispatch and Run Matching

`DispatchWorkflowAsync` sends a workflow dispatch request for the requested
workflow file and branch. It validates all required values and requires a
successful GitHub response.

`GetLatestWorkflowRunAsync` retrieves up to 20 runs for the requested branch,
then `GitHubWorkflowRunMapper`:

1. filters by `headSha` when supplied;
2. prefers runs whose path matches the requested workflow file;
3. falls back to all matching SHA/branch runs when no preferred path exists;
4. selects the newest run by `created_at`;
5. maps the result to `GitHubWorkflowRunDto`.

The commit SHA filter is important for deployment correctness: the monitor
should associate a run with the commit that contains the generated workflow
files rather than an unrelated recent run on the same branch.

`GitHubWorkflowMonitor` polls every ten seconds for up to 60 attempts. It
streams status changes through `ILogStreamer`, persists the run ID, and
downloads logs after completion. The GitHub module only retrieves and maps
the run; orchestration owns polling policy and deployment status transitions.

## Workflow Logs

`DownloadWorkflowRunLogsAsync` requests the workflow run log archive. A
non-success response returns `null` and logs a warning; successful responses
are passed to `GitHubWorkflowLogReader`.

The log reader:

- opens the ZIP stream without extracting files to disk;
- ignores directory-only entries;
- orders entries by full path for stable output;
- adds a `===== path =====` heading for each log file;
- reads UTF-8 text while honoring cancellation;
- returns `null` when the archive contains no readable files.

The resulting text is sent to `ILogStreamer` by orchestration. GitHub does
not directly know about SignalR, Blazor, or client connections.

## Error and Cancellation Rules

- Validate required arguments before making external calls.
- Pass cancellation tokens to every HTTP, Octokit, cache, ZIP, and stream
  operation.
- Preserve `OperationCanceledException`; do not convert cancellation into a
  successful deployment.
- Use `EnsureSuccessStatusCode` for mutating REST operations whose caller
  must stop on failure.
- Keep repository browsing's empty-list fallback behavior unless its public
  contract is intentionally changed.
- Log repository, branch, workflow, and run identifiers as useful context,
  but never log access tokens or plaintext repository secrets.
- Do not add broad catches that turn failed commits, secret writes, dispatches,
  or workflow queries into success-shaped results.

The different failure behavior between repository browsing and deployment
operations is intentional: browsing can display an empty/error state, while
deployment preparation must stop when GitHub cannot perform a required
mutation.

## Security Rules

- Treat GitHub access tokens as credentials at every boundary.
- Do not include tokens in logs, cache values, URLs, exception messages, or
  generated files.
- Keep cache keys token-derived but non-reversible, as implemented by
  `GitHubRepositoryCache`.
- Encrypt Actions secret values with the repository public key before sending
  them to GitHub.
- Do not log plaintext secret values, encrypted payloads, or complete request
  bodies.
- Use the least GitHub OAuth scopes required by the feature. Provider scope
  configuration belongs in `Web.Configs.ServiceConfiguration`.
- Keep OAuth challenge, callback, claims extraction, and cookie creation in
  `Web`; this module starts after an access token is already available.
- Normalize file paths before committing and validate names supplied by
  callers.

## Consumers and Boundaries

| Consumer | GitHub responsibility |
|---|---|
| `Web.Components.Pages.GitHubRepos` | Displays repositories and saves selected repository metadata through `IApplicationService`. |
| `Services.Orchestration.CloudDeploymentOrchestrator` | Writes secrets, commits generated deployment files, records commit/run identifiers, and starts cloud workflow monitoring. |
| `Services.Orchestration.GitHubWorkflowMonitor` | Polls matching runs and streams downloaded logs. |
| `Services.Auth` | Persists the GitHub profile and access token; it does not call this service for OAuth callbacks. |
| `Services.Data.Users` | Retrieves the connected user's GitHub token for authorized callers. |
| `Core.DTO` | Defines stable repository and workflow-run contracts. |

Do not place template generation, Azure provisioning, deployment state
transitions, or UI navigation in `GitHubService`. Keep those concerns in their
existing modules and use `IGitHubService` as the integration boundary.

## Testing Guidance

Prefer testing the public service with a mocked `HttpMessageHandler`,
in-memory distributed cache, and isolated Octokit boundary where practical.
Important scenarios include:

- empty-token and invalid-argument validation;
- repository cache hit, forced refresh, expiry, malformed cache data, and
  unavailable Redis;
- API response deserialization and non-success status handling;
- branch creation and existing-branch commit behavior;
- path normalization and empty generated-file rejection;
- repository public-key validation and sealed-box secret payload creation;
- workflow dispatch and run selection by branch, workflow path, and commit SHA;
- deterministic ZIP log flattening and cancellation;
- cancellation propagation through every external operation.

Do not send real repository mutations or real secrets from the default test
suite. Use disposable repositories or provider mocks for explicit
integration tests.

## Extending the Module

When adding a GitHub capability:

1. Decide whether it is repository browsing, Git mutation, Actions control,
   or an OAuth concern owned by `Web`.
2. Add a narrow method to `IGitHubService` only when the operation is a stable
   cross-module contract.
3. Use `GitHubApiRequestFactory` for new REST calls.
4. Add internal response/request records for only the fields required.
5. Validate inputs and preserve cancellation before external calls.
6. Choose explicit failure semantics: empty result for safe browsing or
   propagated failure for required deployment mutations.
7. Redact tokens and secret values from logs and diagnostics.
8. Update the orchestrator, DTOs, tests, and module documentation together.

If a new operation needs a different GitHub client surface, isolate that
choice behind `IGitHubService` rather than exposing Octokit or raw
`HttpClient` to callers.

## File Map

| File | Purpose |
|---|---|
| `IGitHubService.cs` | Public repository, Git, Actions, and log integration contract. |
| `GitHubService.cs` | Main GitHub adapter using REST and Octokit. |
| `GitHubApiRequestFactory.cs` | Authenticated REST request headers and URI escaping. |
| `GitHubRepositoryCache.cs` | Ten-minute token-hash-keyed repository cache. |
| `GitHubSecretEncryptor.cs` | Sodium sealed-box encryption for Actions secrets. |
| `GitHubRepositorySecretModels.cs` | Internal public-key, secret, and dispatch request records. |
| `GitHubWorkflowRunModels.cs` | Internal workflow-run API response records. |
| `GitHubWorkflowRunMapper.cs` | Workflow run filtering and DTO mapping. |
| `GitHubWorkflowLogReader.cs` | ZIP archive flattening for workflow logs. |

## Related Documentation

- [`Services`](../README.md)
- [`Services/Auth`](../Auth/README.md)
- [`Services/Data`](../Data/README.md)
- `Services/Orchestration` when its module README is added
- `Services/Templating` when its module README is added
- `Services/LogStreaming` when its module README is added
- [`Core/DTO`](../../Core/DTO/README.md)
- [`Web`](../../Web/README.md)
- [Solution navigation map](../../.agents/navigation.md)
