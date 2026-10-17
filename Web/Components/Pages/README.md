# Web/Components/Pages

Dashboard deployment admission navigates to Project Details within the current interactive Blazor application.
It does not open another browser tab or force a document reload. Project route changes retain the existing
cancellation/generation fences. The latest deployment's saved logs can replay after failure or stop as well as while
running; live collection leases still require an active latest deployment. History channel changes issue a fresh
server-filtered page, resetting the cursor while preserving the selected channel across metric-only refreshes.

Dashboard inventory and Project Details metadata refreshes coalesce overlapping notifications into a serial read plus
one fresh pending pass, each using an independent operation scope. Queue/status callbacks read and mutate UI state
only on the renderer dispatcher; failures are observed and disposed pages reject late notifications. Metadata failures
show a Try again action without terminating the circuit or clearing existing terminals. Azure `cloud-web` CPU/memory
is labeled as the Container App total, including shared database containers, rather than attributed to the web process.
`Test/Web/Projects/ProjectDetailsNotificationTests.cs` exercises recovery and dispatcher reentrancy.

Related files are grouped by responsibility. Existing .NET namespaces and public identities are preserved.

- `Authentication/` — `Authentication/LoginForm.razor`, `Authentication/LoginForm.razor.cs`,
  `Authentication/LoginForm.razor.css`, `Authentication/RegistryForm.razor`, `Authentication/RegistryForm.razor.cs`,
  `Authentication/RegistryForm.razor.css`, `Authentication/VerifyEmail.razor`, `Authentication/VerifyEmail.razor.cs`.
- `Errors/` — `Errors/Error.razor`, `Errors/Error.razor.cs`, `Errors/NotFound.razor`.
- `Identity/` — `Identity/AuthenticatedUserDetails.cs`, `Identity/AuthenticatedUserResolver.cs`.
- `Projects/` — `Projects/ProjectDetails.razor`, `Projects/ProjectDetails.razor.cs`,
  `Projects/ProjectDetails.razor.css`, `Projects/DeploymentHistory.razor`, `Projects/DeploymentHistory.razor.css`,
  `Projects/CloudDeploymentPageDefaults.cs`, `Projects/DeploymentMetricDisplay.cs`.
- `Repositories/` — `Repositories/GitHubRepos.razor`, `Repositories/GitHubRepos.razor.cs`,
  `Repositories/GitHubRepos.razor.css`, `Repositories/LocalGitRepos.razor`, `Repositories/LocalGitRepos.razor.cs`,
  `Repositories/LocalGitRepos.razor.css`.
- `Workspace/` — `Workspace/Dashboard.razor`, `Workspace/Dashboard.razor.cs`, `Workspace/Dashboard.razor.css`,
  `Workspace/Home.razor`, `Workspace/Home.razor.css`.

## Operation lifetimes and project reads

Repository and deployment pages use independent scopes for EF-backed owner, save, delete, connection and admission
operations. Interactive registration/verification forms also use one scope per auth operation, retaining their existing
submission guards, default cancellation behavior, navigation and registration compensation.

Project Details reads current metadata plus all-history SQL aggregates and six-row history pages through
IProjectDetailsQuery. Deploy/stop inventory actions and historical consent metadata reads use IProjectActionQuery.
Production registers both; older host/test
composition can retain the original IApplicationService read path. Historical totals, first deployment, outcome meaning,
latest timestamp ties and history ordering are unchanged. Paging now performs a bounded metadata read.

Cloud credentials and provider request assembly are encapsulated by the Web preparation/Application admission boundary.
Pages retain messages, dialogs, page-owned idempotency keys and navigation. DeploymentMetricDisplay also owns the
existing
metric-track parsing/formatting. Project terminal buffering and deduplication live in ProjectTerminalReplayState.
