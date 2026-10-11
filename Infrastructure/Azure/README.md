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
- `AzureManagedDatabaseClient.cs`
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

## Azure runtime collection repair

Container Apps console/system queries use `properties.managedEnvironmentId` from the ARM app response because
Azure associates those tables with the managed environment. The app name and persisted revision still filter every
query. An ARM metrics failure no longer suppresses log collection; safe availability notices identify read failures.
Azure Monitor partial query responses are rejected without advancing durable cursors.

`AzureManagedDatabaseClient` discovers PostgreSQL, MySQL, SQL Database, Cosmos MongoDB and Redis resources only when
both `automate-project-id` and `automate-container-app` tags match the registered deployment. Discovery is bounded
and rejects incomplete inventories; it never reads connection strings. Database failures are isolated per resource.
New templates export database resource logs to the shared workspace's AzureDiagnostics table. Database log cursors
reuse the existing checkpoint store with a hashed `db-` source identity and advance only after gateway confirmation.
Database events use typed Database identity and the Azure Databases terminal; native CPU/memory/RU percentages and
Redis memory bytes use the shared allow-listed telemetry pipeline. No core values are inferred from percentages.

Deploy updated Web and Telemetry binaries together to enable the new metric names. Regenerate/redeploy infrastructure
for database ownership tags and diagnostic export, or apply equivalent tags/settings to verified existing resources.
The connected Azure user needs resource-group/resource/metrics read access and Monitor Logs access to the environment
and database resources/workspace; the deployment identity needs diagnostic-setting write permission. Collection uses
Azure ingestion latency and the configured runtime poll cadence, not an instantaneous console socket. Provider-specific
logging must also be enabled for categories that require it (for example MySQL slow/audit logs or SQL auditing).
Enabling export alone does not generate otherwise disabled database logs. Azure logging ingestion can incur charges.

Deployment preparation registers Microsoft.Insights before GitHub publication; registration may require Azure
propagation time before the first diagnostic-setting write. Registration permission errors use fixed guidance.
Operational export defaults are Cosmos MongoRequests, PostgreSQLLogs and SQL Errors/Timeouts/Deadlocks; MySQL/Redis
remain metrics-only. Database projections retain bounded operation/status/duration/RU fields, excluding query text,
parameters, documents and diagnostic graphs; existing Cosmos CDBMongoRequests tables are also supported.

Inventory follows at most ten trusted ARM pages per list, explicitly enumerates SQL child databases, and requires
both ownership tags on every collected resource. Shared DB observations belong to the latest verified Running
deployment. Missing deployment revision identity never falls back to the app's newest revision.

AzureLogIngestionCursors adds nullable ingestion time/hash fields. Pages use ingestion order, retain original event
timestamps and stable event IDs, and advance only after durable acknowledgement. Partial reads and failed writes
retain the cursor. Web metrics sum podName dimensions within the exact revision; Cosmos uses Maximum normalized RU.
Native minute timestamps and units survive persistence and typed live delivery. Cosmos CPU/memory are unsupported,
not zero. Permission/storage/collection notices appear outside project terminal output.

Legacy ContainerApp custom tables can have an empty _ResourceId. Collection therefore resolves the verified managed
environment's Log Analytics customer ID through ARM, then queries that workspace with exact app/revision filters. Native
and legacy table schemas are normalized through a table-pattern union, avoiding missing-optional-table warnings; genuine
partial errors still reject the page. Cursor hashes use Kusto's canonical timestamp string. Metric windows use completed
UTC minute boundaries so overlapping polls retain stable provider bucket identities.

Ingestion cursors are binned to PostgreSQL's microsecond precision before hash ordering; original event timestamps
remain unchanged. Equal-time hash comparisons use Kusto strcmp, including database cursors. Workspace reads additionally
filter the ARM-derived environment domain/name.
