# Azure

`Services/Azure` prepares and observes AutoMate's Azure Container Apps
deployments. It uses a connected user's delegated Azure Resource Manager
access token to provision the identity and permissions required by a
GitHub Actions OIDC deployment, then polls the deployed Container App for
availability and runtime metrics.

The module does **not** run the complete cloud deployment workflow. GitHub
repository operations, template generation, workflow polling, deployment
record persistence, and UI streaming coordination remain in their respective
`Services.GitHub`, `Services.Templating`, `Services.Orchestration`, and
`Services.LogStreaming`/`Web` modules.

## Responsibilities

- Create or update the Azure resource group used by a deployment.
- Create or update a user-assigned managed identity.
- Register Azure resource providers required by the selected deployment.
- Create or update an exact GitHub Actions federated identity credential.
- Assign the Azure Contributor role to the deployment identity at resource
  group scope.
- Return the OIDC values required by the generated GitHub Actions workflow.
- Read Container Apps revision/ingress state through ARM.
- Read recent CPU and memory metrics through Azure Monitor.
- Publish runtime availability and metrics through `ILogStreamer`.

## Public Contracts

### `IAzureDeploymentOrchestrator`

The public setup contract is:

```csharp
Task<AzureOidcSetupResultDto> EnsureFederatedIdentityAsync(
    AzureCloudCredentialsDto credentials,
    DeploymentConfigDto config,
    string repositoryOwner,
    string repositoryName,
    string branchName,
    CancellationToken cancellationToken = default);
```

It ensures Azure-side prerequisites and returns
`AzureOidcSetupResultDto`. It does not create GitHub repository secrets,
commit workflow files, dispatch a workflow, or wait for the application to
become available.

### `IAzureContainerAppRuntimeStreamer`

The runtime contract starts a per-project background poller:

```csharp
void StartStreaming(
    AzureCloudCredentialsDto credentials,
    DeploymentConfigDto config);
```

The method returns immediately. It validates the minimum resource
identifiers, replaces any existing stream for the same `ProjectId`, and
publishes updates through `ILogStreamer`.

## Cloud Deployment Setup Flow

`AzureDeploymentOrchestrator.EnsureFederatedIdentityAsync` runs the following
sequence:

```text
AzureCloudCredentialsDto + DeploymentConfigDto
    -> AzureOidcSetupPlanner
    -> required resource-provider registration
    -> resource group upsert
    -> user-assigned managed identity upsert
    -> federated credential PUT + readiness check
    -> Contributor role assignment at resource-group scope
    -> AzureOidcSetupResultDto
```

The wider cloud workflow is coordinated by
`Services.Orchestration.CloudDeploymentOrchestrator`:

1. Validate the cloud request and apply cloud resource-name defaults.
2. Call `IAzureDeploymentOrchestrator`.
3. Convert the returned OIDC values into GitHub repository secrets.
4. Generate cloud Docker, Bicep, and workflow files.
5. Commit those files to the configured deployment branch.
6. Poll the GitHub Actions workflow.
7. Start Azure runtime streaming only after a successful workflow result.

Azure setup success means that Azure trust and permissions are prepared. It
does not mean the GitHub workflow or Container App deployment has succeeded.

## Azure Resources and Permissions

### Resource group

The resource group is created or updated in the connected subscription and
uses `DeploymentConfigDto.CloudAzureRegion`. The configured resource-group
name is also used when:

- deriving deterministic managed-identity names;
- assigning the Contributor role scope;
- generating Bicep resource names;
- resolving the runtime Container App resource ID.

### User-assigned managed identity

The identity is created or updated in the deployment resource group. GitHub
Actions uses its client ID for Azure login through OIDC. Its principal ID is
used for RBAC assignment.

### Resource providers

Every Container Apps deployment requires:

- `Microsoft.App`
- `Microsoft.OperationalInsights`

Database configuration can add provider namespaces:

| Database type | Provider |
|---|---|
| PostgreSQL | `Microsoft.DBforPostgreSQL` |
| MySQL | `Microsoft.DBforMySQL` |
| SQL Server | `Microsoft.Sql` |
| MongoDB | `Microsoft.DocumentDB` |
| Redis | `Microsoft.Cache` |

Registration is idempotent. Already registered providers are skipped;
otherwise the module requests registration and polls until Azure reports
`Registered`. The current maximum is 24 attempts with a five-second delay.
Unauthorized or forbidden registration failures produce an actionable error
explaining that manual registration or broader subscription permissions may
be required.

### Role assignment

The managed identity receives the built-in Azure Contributor role at the
target resource-group scope. The assignment resource name is a deterministic
GUID derived from scope, principal ID, and role definition ID, so repeated
setup calls target the same ARM resource instead of creating duplicates.

The connected Azure user must be able to assign roles. In practice this
requires Owner or User Access Administrator permissions at the relevant
scope.

## GitHub Actions OIDC Contract

The federated credential is created with exact values:

| Field | Value |
|---|---|
| Issuer | `https://token.actions.githubusercontent.com` |
| Subject | `repo:{owner}/{repository}:ref:refs/heads/{branch}` |
| Audience | `api://AzureADTokenExchange` |

The subject is branch-specific. Changing the deployment branch requires a
matching federated credential subject and workflow branch configuration.

`AzureOidcSetupPlanner` derives:

- a deterministic resource-group context;
- a normalized managed-identity name;
- a normalized federated-credential name;
- a short SHA-256 suffix to avoid collisions while keeping names compact.

Azure identity names are lowercased, non-alphanumeric characters become
hyphens, repeated hyphens are removed, and names are limited to 80
characters.

`AzureFederatedCredentialService` uses direct ARM REST rather than relying
only on the Azure SDK. This preserves the issuer string exactly and performs
a read-back check for issuer, subject, and audience. Read-back failures during
the local propagation window are logged, but the setup continues after the
bounded readiness timeout because Azure may still be propagating the
credential.

## Runtime Streaming

`AzureContainerAppRuntimeStreamer` starts one worker per AutoMate project.
When a new stream starts for a project, the previous worker is cancelled.
The worker:

1. Builds the Container App ARM resource ID from subscription, resource group,
   and app name.
2. Reads the latest ready revision and ingress FQDN.
3. Publishes an availability message only when revision or FQDN changes.
4. Reads the last five minutes of Azure Monitor data at one-minute intervals.
5. Publishes average CPU and memory metrics.
6. Waits 30 seconds before polling again.

Messages are sent through `ILogStreamer` using the existing cloud container
name `cloud-web`, allowing the Web SignalR terminal to display cloud runtime
data alongside local deployment streams.

Missing metric samples are rendered as `n/a`. CPU is displayed in cores and
memory is converted from bytes to mebibytes using invariant formatting.

Runtime streaming stops when its cancellation token is cancelled or when the
polling loop exits after an unexpected error. The active-stream dictionary is
cleaned up only if the completing worker still owns the registration, which
prevents an older worker from removing a replacement stream.

## SDK and REST Split

The module intentionally uses both Azure SDK and direct ARM REST:

| Operation | Implementation |
|---|---|
| Subscription resource creation | Azure Resource Manager SDK |
| Resource-group upsert | Azure Resource Manager SDK |
| Managed-identity upsert | Azure Resource Manager SDK |
| Federated credential create/read | Direct ARM REST |
| Resource-provider registration/polling | Direct ARM REST |
| Contributor role assignment | Direct ARM REST |
| Container App state reads | Direct ARM REST |
| Azure Monitor metrics reads | Direct ARM REST |

`StaticAccessTokenCredential` adapts the already-issued delegated OAuth
access token for SDK calls. It exposes the token for a short 30-minute
credential lifetime and honors cancellation. It does not refresh tokens; the
caller must provide a valid token.

Do not replace direct REST calls with SDK abstractions without verifying that
the required API version, exact OIDC issuer behavior, and response semantics
remain unchanged.

## Inputs and Defaults

The module consumes:

- `AzureCloudCredentialsDto.TenantId`
- `AzureCloudCredentialsDto.SubscriptionId`
- `AzureCloudCredentialsDto.AccessToken`
- `DeploymentConfigDto.CloudAzureRegion`
- `DeploymentConfigDto.CloudResourceGroupName`
- `DeploymentConfigDto.CloudContainerAppName`
- `DeploymentConfigDto.CloudRegistryName` indirectly through templates
- `DeploymentConfigDto.Databases` for provider registration

Cloud resource defaults are applied by
`Services.Orchestration.CloudDeploymentDefaults` before Azure setup. The
Azure module still validates essential credentials and resource-group input
through `AzureOidcSetupPlanner`; it must not assume that every caller came
through the normal cloud orchestrator.

Runtime streaming requires a non-empty access token, subscription ID,
resource-group name, and Container App name. Invalid runtime targets are
ignored by `StartStreaming` because there is no synchronous result channel;
callers should validate cloud configuration before starting a stream.

## Security Model

AutoMate uses delegated Azure OAuth for preparation and GitHub Actions OIDC
for the deployment workflow:

```text
User Azure OAuth token
    -> AutoMate prepares Azure resources and trust

GitHub Actions OIDC token
    -> Azure federated credential
    -> user-assigned managed identity
    -> scoped Contributor access on resource group
```

Rules:

- Never log access tokens or serialize `AzureCloudCredentialsDto` directly.
- Do not persist Azure client secrets for deployment.
- Keep Azure access/refresh token encryption in `Services.Data`.
- Use the smallest practical ARM scope for new role assignments.
- Preserve exact issuer, subject, and audience matching.
- Do not put GitHub repository PATs into this module; those belong to the
  GitHub/orchestration flow.
- Do not expose ARM response bodies to users unless they are safe and
  actionable; response text may contain tenant or resource details.
- Pass cancellation tokens through all ARM and polling operations.

The Contributor assignment is intentionally scoped to the deployment resource
group rather than the entire subscription. Any broader permission should be
treated as a security-sensitive design change.

## Error Handling and Observability

The module fails setup explicitly when required infrastructure cannot be
prepared:

- invalid credentials or missing setup values throw argument exceptions;
- failed ARM resource operations propagate SDK or HTTP failures;
- provider registration timeout throws an actionable
  `InvalidOperationException`;
- role-assignment permission failures explain the required Azure permission;
- federated-credential create/update failures include Azure's response text;
- readiness propagation uncertainty is logged as a warning and does not
  automatically fail setup;
- runtime polling logs cancellation at information level and unexpected
  failures at error level.

Avoid broad catches that turn failed resource provisioning into a successful
OIDC result. Bounded retries are appropriate for Azure propagation; they must
have a clear attempt limit and cancellation support.

## Dependency Injection and Lifetimes

`Web.Configs.ServiceConfiguration.RegisterDomainServices` registers:

```csharp
services.AddScoped<IAzureDeploymentOrchestrator, AzureDeploymentOrchestrator>();
services.AddScoped<IAzureContainerAppRuntimeStreamer, AzureContainerAppRuntimeStreamer>();
```

The public services are scoped. Internal helpers are created by their owning
service and are not registered independently. `IHttpClientFactory` supplies
HTTP clients for ARM and Monitor calls, while `ILogger` provides structured
diagnostics.

Runtime stream cancellation sources are held in a static
`ConcurrentDictionary<Guid, CancellationTokenSource>`, so the active stream
registry is shared across service scopes. Any future lifecycle or shutdown
change must account for that static ownership explicitly.

## File Map

| File | Purpose |
|---|---|
| `IAzureDeploymentOrchestrator.cs` | Public Azure OIDC/resource setup contract. |
| `AzureDeploymentOrchestrator.cs` | Coordinates provider registration, identity, OIDC, and RBAC setup. |
| `AzureConstants.cs` | Shared ARM endpoints, API versions, OIDC values, role IDs, and polling delay. |
| `AzureOidcSetupPlanner.cs` | Validates inputs and derives deterministic names/claims. |
| `AzureOidcSetupContext.cs` | Immutable setup-operation context. |
| `AzureManagedIdentityProvisioner.cs` | Azure SDK resource-group and identity upserts. |
| `AzureFederatedCredentialService.cs` | ARM REST federated-credential creation and readiness verification. |
| `AzureResourceProviderRegistrar.cs` | Provider registration and readiness polling. |
| `AzureRoleAssignmentService.cs` | Deterministic Contributor role assignment. |
| `StaticAccessTokenCredential.cs` | Azure SDK adapter for an existing OAuth token. |
| `IAzureContainerAppRuntimeStreamer.cs` | Public runtime telemetry contract. |
| `AzureContainerAppRuntimeStreamer.cs` | Per-project background polling and log-stream publication. |
| `AzureContainerAppClient.cs` | ARM state and Azure Monitor metric reads. |
| `AzureContainerAppState.cs` | Internal revision/FQDN snapshot. |
| `AzureContainerAppMetrics.cs` | Internal CPU/memory display snapshot. |

## Extending the Module

Before adding an Azure capability:

1. Decide whether it is deployment preparation, runtime observation, or
   orchestration; keep workflow coordination outside this module.
2. Choose the Azure SDK or direct ARM REST based on API coverage and exact
   request/response requirements.
3. Add API versions and immutable provider constants to `AzureConstants`
   rather than scattering literals.
4. Validate required IDs, names, and tokens before making ARM calls.
5. Make repeated setup calls idempotent and use deterministic resource names
   where possible.
6. Bound all propagation polling and honor cancellation.
7. Keep credentials out of logs and return DTOs.
8. Update generated workflow secrets, Bicep/template models, and
   `CloudDeploymentOrchestrator` when a new resource or output is required.
9. Update Azure permissions and security documentation for every new role or
   scope.

For new runtime telemetry, add a narrow internal client result and publish
through `ILogStreamer`; do not couple Azure classes directly to SignalR or
Blazor.

## Related Documentation

- [`Services`](../README.md)
- [`Core/DTO`](../../Core/DTO/README.md)
- [`Core/Entities`](../../Core/Entities/README.md)
- [`Web`](../../Web/README.md)
- [Solution navigation map](../../.agents/navigation.md)
