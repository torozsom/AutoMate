# Azure Container Apps terminal observability research

Researched on 2026-10-11 against Microsoft documentation, the public Azure CLI implementation, and the current AutoMate
source. This is a research and implementation recommendation; the customer's deployed Azure resources and effective
permissions were not queried.

## Implementation follow-up

The original research below describes the pre-change collector. The implementation now adds independent ACA live
console/system HTTP streams, token renewal, replica reconciliation, ownership expiry, bounded retry deduplication,
exact recent live/history console reconciliation, app-level system event handling and timestamped metric terminal
summaries. Historical replay and managed database collection remain active. See
[the Azure module README](../Infrastructure/Azure/README.md#aca-live-terminals-2026-10-11) for current operation and
validation.

Read-only Azure verification on 2026-10-11 found an active newer revision, `mongowebapp-prod-app--am-38159892738`, with
zero replicas, a working system stream and a Log Analytics destination. The 2024-03-01 token/stream requests returned
HTTP 200. Real system envelopes use `Msg`, which the adapter now supports. An aggregate-only workspace query showed
older console records; neither a running console fixture nor restarted-Web/browser delivery was verified against
this app. No infrastructure configuration or Azure permissions were changed.

## Finding

The message `Azure Container App is available at ... Latest ready revision: ...` is an AutoMate lifecycle event produced
by `PollStateAndMetricsAsync`, not a log retrieved from the container. Its presence confirms that resource-state
retrieval and at least one terminal-delivery path worked. It does not establish that console queries succeeded, returned
records, or reached durable storage.

`AzureContainerAppRuntimeStreamer` currently polls stored Azure Monitor Logs. It has no ACA live-stream connection. Its
normal cadence is 60 seconds, overridden by `TelemetryStorageOptions.RuntimeSampleSeconds`; each target poll has a
45-second deadline. Log Analytics records can arrive several minutes after emission. The ACA portal documentation
separates current output, historical queries, metrics, OpenTelemetry, and resource-operation audit events into distinct
facilities. [ACA portal observability](https://containerapps.azure.com/docs/containerapps/observe), [Azure logging destinations](https://learn.microsoft.com/en-us/azure/container-apps/log-options).

## Sources and terminal destinations

| Source                                                      | Retrieval                                                                    | AutoMate destination                                                       |
|-------------------------------------------------------------|------------------------------------------------------------------------------|----------------------------------------------------------------------------|
| Web container stdout/stderr                                 | ACA console live stream; Log Analytics for history                           | Azure Web terminal                                                         |
| Database container stdout/stderr, if actually hosted in ACA | Its own app/revision/replica/container stream and historical console records | Azure Databases terminal, retaining container identity                     |
| ACA lifecycle/platform events                               | App system event stream; stored system logs                                  | Azure System terminal                                                      |
| ACA platform CPU/memory and other metrics                   | Azure Monitor Metrics API                                                    | Existing metric display/charts, optionally a rate-limited terminal summary |
| Managed database diagnostics                                | Database diagnostic settings and Azure Monitor Logs                          | Azure Databases terminal                                                   |
| Managed database performance                                | Database-specific Azure Monitor Metrics API                                  | Database metrics display/charts                                            |

Console records are output written to stdout/stderr. File-only application logging is not automatically equivalent to
console output. ACA system events describe platform behavior; they do not replace application
logs. [Console and system logs](https://learn.microsoft.com/en-us/azure/container-apps/log-monitoring).

## Live ACA collection

Microsoft documents `az containerapp logs show --type console --follow` and `--type system --follow`. Console streaming
selects a revision, replica, and container. A single selected stream is not a subscription to all replicas. Tail is
limited to 300 lines and is not a durable historical
archive. [CLI reference](https://learn.microsoft.com/en-us/cli/azure/containerapp/logs?view=azure-cli-latest).

For a .NET collector:

1. Resolve the deployment's verified app and revision; enumerate current replicas and their containers.
2. Obtain a short-lived ACA token through ARM `POST {appResourceId}/getAuthtoken?api-version={supportedVersion}`. This
   returns `properties.token` and `properties.expires`.
3. Discover the stream endpoint from Azure responses. Replica container metadata includes `logStreamEndpoint`; the app
   also exposes `eventStreamEndpoint`.
4. Open a streaming HTTP GET with the ACA token, read incrementally, and normalize records into the existing diagnostic
   publisher.
5. Supervise connections independently of metric/history polling. Reconnect with bounded backoff, refresh credentials,
   rediscover replicas after scaling/restarts, and report a scaled-to-zero app explicitly.

The public Azure CLI currently uses a streaming HTTP response with bearer authorization and `follow`, `output`, and
`tailLines` parameters. It derives console and system endpoint paths from the returned event-stream base URL. This is
evidence for implementation, not a reason to assume an undocumented transport contract is permanent. Verify the selected
API version and endpoint behavior before
shipping. [Get auth token](https://learn.microsoft.com/en-us/rest/api/resource-manager/containerapps/container-apps/get-auth-token?view=rest-resource-manager-containerapps-2026-01-01), [Replica metadata](https://learn.microsoft.com/en-us/rest/api/resource-manager/containerapps/container-apps-revision-replicas/get-replica?view=rest-resource-manager-containerapps-2026-01-01), [Azure CLI implementation](https://github.com/Azure/azure-cli/blob/dev/src/azure-cli/azure/cli/command_modules/containerapp/custom.py).

Keep Azure tokens on the backend. Connect the browser through AutoMate's existing SignalR channel. Preserve app,
revision, replica, container, timestamp, and stdout/stderr identity. A database container needs an explicit
ownership/routing mapping; name guessing is insufficient.

Live streaming works even when stored logging is disabled. Historical replay still needs stored logs. Combining both
requires an explicit duplicate policy: two sources may represent the same emitted record without sharing a provider
event ID. [Logging destinations](https://learn.microsoft.com/en-us/azure/container-apps/log-options).

## Historical collection and permissions

AutoMate already normalizes both `ContainerAppConsoleLogs_CL` / `ContainerAppSystemLogs_CL` and native
`ContainerAppConsoleLogs` / `ContainerAppSystemLogs` schemas with table-pattern unions. Do not propose schema support as
a missing feature.

The current template provisions an environment with `destination: 'log-analytics'` and a workspace customer ID. The
collector resolves that workspace and uses `/v1/workspaces/{workspaceId}/query`. For an environment switched to
`azure-monitor`, log categories must be exported through environment diagnostic settings. Current Microsoft guidance
says app-level diagnostic settings export metrics, while console/system log export is configured at the environment. The
current fallback queries the environment resource; workspace discovery for arbitrary diagnostic-setting destinations is
not
implemented. [Destination configuration](https://learn.microsoft.com/en-us/azure/container-apps/log-options), [Resource-context queries](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/api/azure-resource-queries).

The existing delegated token exchange uses `https://api.loganalytics.io/.default`. It requires Log Analytics API
`Data.Read` consent and effective access for the connected user to the queried workspace/resources. An ARM token that
successfully reads app state does not prove Logs API authorization. Legacy records without resource attribution require
a suitable workspace query scope and workspace
permission. [Logs API authentication](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/api/access-api).

Live streaming has separate permissions. Microsoft's Container Apps Operator role includes action permissions and
`Microsoft.App/containerApps/logstream/action` as a data action, along with exec/debug capabilities. Prefer a narrowly
scoped permission set where appropriate; do not assume Reader or Log Analytics Reader grants live
streaming. [Built-in container roles](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/containers).

## Concrete implementation gaps and investigation candidates

These are code observations, not a confirmed diagnosis of the deployed app:

- `TailSourceAsync` and its KQL require an exact revision for both console and system records. App/environment events
  with empty or different revision identities are excluded. Keep console revision isolation; give app-level system
  events a separate, ownership-checked scope and avoid attributing them to the wrong deployment. The CLI itself rejects
  revision/replica/container selectors for system
  streaming. [Azure CLI implementation](https://github.com/Azure/azure-cli/blob/dev/src/azure-cli/azure/cli/command_modules/containerapp/custom.py).
- All ACA console records are routed to `cloud-web`, even though the source container name is retained in attributes. A
  database sidecar or separate database ACA needs explicit collection and routing.
- Environment filtering accepts the ARM environment name and the first segment of its default domain. Check actual
  workspace values before concluding those filters match the deployed schema.
- Initial collection looks back ten minutes plus two minutes of overlap in ingestion time. Startup logs already ingested
  before that window are not replayed. Consider a bounded bootstrap tied to deployment creation and preserve existing
  ingestion cursors afterward.
- A successful empty query clears its issue and produces no collection-status notice. Distinguish empty output, missing
  storage, filtering mismatch, and delayed ingestion in the UI.
- Records advance only after `IDurableDeploymentDiagnosticPublisher.PublishDurablyAsync` succeeds. Check private
  Telemetry gateway health if Azure returns records but terminals stay empty.
- Provider warnings are sent through `ReceiveTerminalNotice`; they appear outside terminal output. Inspect that notice
  area when the web terminal only shows availability.
- The generated app has `minReplicas: 0`. Verify whether a replica exists at observation time. Missing CPU/output is not
  necessarily a collector failure for an idle scaled-to-zero app.

Relevant files: `Infrastructure/Azure/ContainerApps/AzureContainerAppRuntimeStreamer.cs`,
`../Infrastructure/Azure/Monitor/AzureMonitorLogsClient.cs`,
`../Infrastructure/Azure/ContainerApps/AzureContainerAppClient.cs`,
`../Infrastructure/Azure/Monitor/AzureMonitorLogsTokenProvider.cs`,
`../Infrastructure/Azure/Monitor/AzureMonitorLogsOptions.cs`,
`Infrastructure/Diagnostics/Delivery/DeploymentDiagnosticPipeline.cs`, and
`Web/Components/Pages/Projects/ProjectDetails.razor.cs`.

## Database distinction

The generated Azure template provisions PostgreSQL/MySQL Flexible Server, Azure SQL Database, Cosmos DB for MongoDB, or
Azure Cache for Redis as managed resources. It does not deploy those databases as ACA containers. Thus the generated
MongoDB setup has no `mongod` ACA console to tail.

AutoMate already discovers managed databases through matching project/app ownership tags. New templates export
PostgreSQLLogs, SQL Errors/Timeouts/Deadlocks, and Cosmos MongoRequests; MySQL and Redis currently collect metrics only.
Older deployments need equivalent tags/settings or infrastructure regeneration.

Cosmos MongoDB operations can be read from AzureDiagnostics or CDBMongoRequests. These are request diagnostics, not a
MongoDB process console. AutoMate deliberately projects bounded operation/status/duration/RU metadata; full query text
and documents are excluded. Cosmos exposes service metrics such as normalized RU usage; do not invent database CPU or
memory from
them. [MongoDB diagnostic queries](https://learn.microsoft.com/en-us/azure/cosmos-db/mongodb/diagnostic-queries), [CDBMongoRequests schema](https://learn.microsoft.com/en-us/azure/azure-monitor/reference/tables/cdbmongorequests).

## Metrics

The current ACA metric names are correct: `UsageNanoCores` and `WorkingSetBytes`, sampled at one-minute granularity,
split by revision/pod. Convert nano cores to cores by dividing by 1,000,000,000. Memory is bytes. AutoMate already sums
pod dimensions within the exact revision and delivers typed metric snapshots separately from terminal text.

Additional useful platform metrics include Requests, Replicas, RestartCount, RxBytes, and TxBytes. Query definitions and
supported aggregations per metric; do not assume every metric has identical dimensions. Show missing samples as
unavailable, retain source timestamps, and avoid treating every poll as a new measurement. Basic CPU/memory metrics do
not provide a container dimension, so do not label an app/replica aggregate as an individual sidecar's
usage. [ACA metric reference](https://learn.microsoft.com/en-us/azure/azure-monitor/reference/supported-metrics/microsoft-app-containerapps-metrics).

Managed database metrics remain service-specific: existing code requests CPU/memory percentages for Flexible Servers,
SQL CPU percentage, Redis processor/memory gauges, and Cosmos normalized RU percentage.

## Read-only diagnostic sequence

Use the correct Azure subscription and resource group. These commands are operator examples and were not executed
against the customer subscription.

```powershell
$resourceGroup = '<resource-group>'
$appName = 'mongowebapp-prod-app'
$revisionName = 'mongowebapp-prod-app--am-38158203874'

az containerapp show -g $resourceGroup -n $appName --query '{environment:properties.managedEnvironmentId,ready:properties.latestReadyRevisionName,containers:properties.template.containers[].name}'
az containerapp replica list -g $resourceGroup -n $appName --revision $revisionName
az containerapp logs show -g $resourceGroup -n $appName --type system --tail 100 --format json

# Fill these from replica metadata. Add --follow for a continuing live stream.
az containerapp logs show -g $resourceGroup -n $appName --type console --revision $revisionName --replica '<replica>' --container '<container>' --tail 100 --format json
```

Query the verified workspace first with only the app filter, then compare the recorded environment/revision/container
values with AutoMate's filters:

```kusto
union withsource=SourceTable ContainerAppConsoleLogs*
| where TimeGenerated > ago(24h)
| extend AppName=coalesce(tostring(column_ifexists('ContainerAppName', '')), tostring(column_ifexists('ContainerAppName_s', '')))
| where AppName == 'mongowebapp-prod-app'
| order by TimeGenerated desc
| take 100
```

Repeat with `ContainerAppSystemLogs*`. An absent table is a diagnostic result, not equivalent to zero matching rows.
Inspect AzureDatabases diagnostic settings and actual ownership tags separately. Avoid dumping environment secret values
or connection strings.

Interpretation:

- Live output exists, historical query is empty/fails: investigate storage destination, export, ingestion delay,
  consent, and workspace permissions.
- Broad historical query has output, AutoMate query does not: compare app/environment/revision filters, lookback, and
  cursors.
- AutoMate query returns records but UI does not: inspect durable publication, gateway confirmation, terminal channel,
  selected deployment, and SignalR delivery/replay.
- No running replica/output: verify scale state, generate ordinary application traffic, and check whether the
  application writes console logs.
- Managed database metrics work but logs do not: verify category support/export and activity that produces the selected
  diagnostics.

## Recommended implementation order

1. Add collection-status evidence and repair verified stored-query/routing problems first.
2. Add independently supervised ACA live console and system streams through the existing publisher/SignalR boundary.
3. Preserve stored-log backfill/replay, durable cursors, original timestamps, and deployment ownership isolation; define
   reconciliation with live records.
4. Extend service-native database diagnostics and supported metrics only where the deployed provider offers them.
5. Verify with a live ACA fixture: startup output, stdout/stderr, multiple replicas, restart, scale-to-zero/recovery,
   app-level system events, denied permissions, delayed historical ingestion, gateway outage, and reconnect/replay.
   Existing stubbed adapter tests cannot establish real Azure endpoint or permission behavior.
