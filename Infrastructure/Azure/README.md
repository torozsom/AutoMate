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
- `ContainerApps/AzureContainerAppClient.cs`
- `ContainerApps/AzureContainerAppMetrics.cs`
- `Databases/AzureManagedDatabaseClient.cs`
- `ContainerApps/AzureContainerAppRuntimeStreamer.cs`
- `ContainerApps/AzureContainerAppLogCheckpointStore.cs`
- `Monitor/AzureMonitorLogsClient.cs`
- `Monitor/AzureMonitorLogsOptions.cs`
- `Monitor/AzureMonitorLogsTokenProvider.cs`
- `ContainerApps/AzureContainerAppState.cs`
- `Provisioning/AzureDeploymentOrchestrator.cs`
- `Identity/AzureFederatedCredentialService.cs`
- `Identity/AzureManagedIdentityProvisioner.cs`
- `Identity/AzureOidcSetupContext.cs`
- `Identity/AzureOidcSetupPlanner.cs`
- `Provisioning/AzureResourceProviderRegistrar.cs`
- `Identity/AzureRoleAssignmentService.cs`
- `Identity/StaticAccessTokenCredential.cs`

## Boundary

Infrastructure may reference Application and Domain, but never Web. It implements ports and owns provider-specific
behavior.

## Related documentation

- [Solution navigation map](../../navigation.md)

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
and database resources/workspace; the deployment identity needs diagnostic-setting write permission. Historical
collection uses
Azure ingestion latency and the configured runtime poll cadence; ACA live collection uses independent HTTP streams.
Provider-specific
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

## ACA live terminals (2026-10-11)

`AzureContainerAppLiveLogs` supervises console streams for every replica/container of the recorded deployment revision
and the app's system event stream. The hosted runtime coordinator renews ownership at least every sixty seconds,
independently of the configured metric sample cadence. Live collection starts before Monitor Logs token exchange and
does not require a Log Analytics workspace. Streams stop when their deployment is retired, another Web host owns the
collector lease, the ownership heartbeat expires, or the host shuts down.

The adapter uses ARM `getAuthtoken` with API version 2024-03-01, Azure-discovered development endpoints and
`ResponseHeadersRead`. The separate `AzureLiveLogs` HTTP client disables redirects and limits header establishment
to fifteen seconds; body reads are cancellable. Discovery/reconnection occurs every fifteen seconds, with up to
64 console streams per deployment. Readers reconnect at most every ninety seconds and before token expiration.
Provider JSON envelopes use `Log` for console output and `Msg` for system events; provider timestamps remain intact.
Connections, permission failures and zero-replica states appear in the Azure System terminal.

Live records use the same redaction, durable gateway and SignalR pipeline as historical records. Confirmed live event
identities suppress the last 4,096 reconnect-tail records. Historical console rows with exactly matching timestamp,
revision, container, replica, stream and message reuse recent durable live acknowledgement before advancing their
existing ingestion cursor. Missing/different provider identity never triggers heuristic suppression; such records can
still appear from both transports. Gateway failure retains the existing queued live-delivery fallback and does not
mark a record durably acknowledged.

App-level system events without a revision belong only to the latest verified active deployment. Nonempty foreign
revision identities remain excluded. Exact configured database container suffixes route to Azure Databases and are
restored from the deployment snapshot after restart; managed databases retain their existing resource collector.

Each new web CPU/memory observation also produces a timestamped `[METRIC]` terminal summary, using native units and
the original one-minute sample time. Overlapping polling windows do not repeatedly print the same minute in a running
collector. Database observations use numeric telemetry delivery only: database terminals contain operational logs,
not utilization summaries. Existing numeric metric callbacks/charts and managed database diagnostics remain active.

Restart Web with the rebuilt binaries to activate the hosted reader. Live access requires ACA resource/replica reads,
`getAuthtoken` access and container-app log-stream permission; Log Analytics delegated consent remains necessary only
for historical/database queries. The Container Apps Operator built-in role includes streaming plus broader exec/debug
permissions; a custom narrower role may be preferable. No Azure roles or infrastructure resources were changed here.

Validation: solution build and the complete unit suite pass. Read-only checks against the existing customer app
confirmed the real `eventStreamEndpoint`, token expiry, successful 2024-03-01 token/event requests, and `Msg` envelopes.
That app currently has zero replicas; a real active-console/multiple-replica test and an end-to-end browser check using
the restarted Web host remain deployment validation steps. See the research document for diagnostic commands.

## Folder organization

Folders group related files while retaining the module's existing .NET namespaces and public contracts.

- `ContainerApps/` — `ContainerApps/AzureContainerAppClient.cs`, `ContainerApps/AzureContainerAppLiveLogs.cs`,
  `ContainerApps/AzureContainerAppLogCheckpointStore.cs`, `ContainerApps/AzureContainerAppMetrics.cs`,
  `ContainerApps/AzureContainerAppRuntimeStreamer.cs`, `ContainerApps/AzureContainerAppState.cs`.
- `Databases/` — `Databases/AzureManagedDatabaseClient.cs`.
- `Identity/` — `Identity/AzureArmCredentialsProvider.cs`, `Identity/AzureFederatedCredentialService.cs`,
  `Identity/AzureManagedIdentityProvisioner.cs`, `Identity/AzureOidcSetupContext.cs`,
  `Identity/AzureOidcSetupPlanner.cs`, `Identity/AzureRoleAssignmentService.cs`,
  `Identity/StaticAccessTokenCredential.cs`.
- `Monitor/` — `Monitor/AzureMetricObservations.cs`, `Monitor/AzureMonitorLogsClient.cs`,
  `Monitor/AzureMonitorLogsOptions.cs`, `Monitor/AzureMonitorLogsTokenProvider.cs`.
- `Provisioning/` — `Provisioning/AzureContainerRegistryProvisioner.cs`, `Provisioning/AzureDeploymentOrchestrator.cs`,
  `Provisioning/AzureResourceProviderRegistrar.cs`.
