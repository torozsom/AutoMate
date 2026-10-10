# Azure

Azure Container Apps and OIDC infrastructure adapters.
For SaaS, launch workers refresh ARM credentials on demand, ensure a customer ACR exists, assign separate push and pull
identities, and use the pull identity for Container Apps image access. ACR creation and role assignment require the
connected customer's Azure account to have sufficient permission.

Container Apps availability, metrics, console output, and system/revision events are normalized into deployment
diagnostics before terminal delivery. `AzureContainerAppRuntimeStreamer` is a host-managed coordinator; it queries
the Azure Monitor resource-scoped Logs API with a memory-only token and persists timestamp/hash cursors, never log
content or OAuth tokens.
The runtime poll activity and source-only metrics report query failures, subsequent recovery and duplicate checkpoint
suppression. Delivered record age is measured by the shared publisher; live transport failure does not invalidate a
confirmed durable record or prevent its checkpoint from advancing.

## Source inventory

- `AzureConstants.cs`
- `AzureContainerAppClient.cs`
- `AzureContainerAppMetrics.cs`
- `AzureContainerAppRuntimeStreamer.cs`
- `AzureContainerAppLogCheckpointStore.cs`
- `AzureMonitorLogsClient.cs`
- `AzureMonitorLogsOptions.cs`
- `AzureMonitorLogsTokenProvider.cs`
- `AzureContainerAppState.cs`
- `AzureDeploymentOrchestrator.cs`
- `AzureFederatedCredentialService.cs`
- `AzureManagedIdentityProvisioner.cs`
- `AzureOidcSetupContext.cs`
- `AzureOidcSetupPlanner.cs`
- `AzureResourceProviderRegistrar.cs`
- `AzureRoleAssignmentService.cs`
- `StaticAccessTokenCredential.cs`

## Boundary

Infrastructure may reference Application and Domain, but never Web. It implements ports and owns provider-specific
behavior.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)

Runtime polling automatically collects active deployments independently of viewers and legacy preference flags, and
defaults to 60 seconds. Observations are saved for replay. Cloud launch paths, including self-hosted launches, refresh
ARM
credentials at execution time and persist rotated credentials through the protected user mapping. Preparation failures
are published through redaction to the deployment Build terminal. Numeric core/byte observations
use Azure Monitor `UsageNanoCores` divided by one billion and `WorkingSetBytes`, rather than parsing display strings.
See [Microsoft's metric definitions](https://learn.microsoft.com/en-us/azure/container-apps/metrics).

## Deployment telemetry update

Both SelfHosted and SaaS use the private Telemetry disk gateway for new deployment logs and metrics. PostgreSQL payload
writes are rejected at application startup; legacy reads and draining of existing outbox rows remain available. The
gateway confirms durable checksummed writes before cloud checkpoints advance. Tenant-scoped v2 history, deployment
revision recovery and weighted daily project analytics are documented in [the rollout guide](/docs/saas-telemetry.md).
Operational Loki/Mimir data expires after 30 days. Redacted deployment diagnostics and project analytics aggregates
remain until owner deletion; see ADR 0004. See the root navigation.md for new module entry
points.

Runtime polls and child log/metric queries have GUID correlation and finite outcomes. Handled Monitor result failures
mark query spans Error without changing retry/checkpoint behavior. Resource IDs, tokens, KQL, names and records are
excluded from custom tags; coordinator failure logs omit exception bodies. Revision-isolation tests check correlation.

OIDC setup/readiness logs omit provider identity/subject payloads and raw exceptions. Readiness retains attempt counts
and failure types, with the same retries and propagation timeout behavior.

Verified Azure runtime observations enrich only the selected deployment snapshot with its resolved image,
revision/resource identity and validated app URL. Mutable project settings never reconstruct missing historical values.
