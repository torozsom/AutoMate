# Container Apps collection

Host-managed recovery, state/native metrics, independent live streams and durable historical log cursors.

## Contents

| File                                                                             | Types or asset                                                                                                           | Responsibility                                                                                       |
|----------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------|
| [AzureContainerAppClient.cs](AzureContainerAppClient.cs)                         | `AzureContainerAppClient`                                                                                                | Reads Azure Container Apps availability and metrics through Azure Resource Manager REST APIs.        |
| [AzureContainerAppLiveLogs.cs](AzureContainerAppLiveLogs.cs)                     | `AzureContainerAppLiveLogs`, `Session`, `Connection`, `AzureLiveLogTarget`, `AzureLiveLogEndpoint`, `AzureLiveLogRecord` | Supervises ACA console/event HTTP streams independently of historical queries and metric polling.    |
| [AzureContainerAppLogCheckpointStore.cs](AzureContainerAppLogCheckpointStore.cs) | `AzureContainerAppLogCheckpointStore`                                                                                    | Persists Azure Container Apps log cursors without retaining log content.                             |
| [AzureContainerAppMetrics.cs](AzureContainerAppMetrics.cs)                       | `AzureContainerAppMetrics`                                                                                               | Runtime metrics rendered in the deployment log stream for an Azure Container App.                    |
| [AzureContainerAppRuntimeStreamer.cs](AzureContainerAppRuntimeStreamer.cs)       | `AzureContainerAppRuntimeStreamer`                                                                                       | Host-managed coordinator for Azure Container Apps state, metrics, console output, and system events. |
| [AzureContainerAppState.cs](AzureContainerAppState.cs)                           | `AzureContainerAppState`, `AzureContainerAppLogScope`                                                                    | Runtime availability state read from an Azure Container App.                                         |

| Module                       | Responsibility                                                  |
|------------------------------|-----------------------------------------------------------------|
| [Runtime](Runtime/README.md) | Per-target database collection, availability and cursor policy. |

## Usage and integration

AzureContainerAppRuntimeStreamer coordinates verified deployment targets and scoped passes. AzureContainerAppClient
resolves ARM state, managed environment/log scope and native metrics. AzureContainerAppLiveLogs supervises replica
console/system HTTP streams. CheckpointStore persists progress only; Runtime collaborators handle database passes,
availability and identity/cursor policy.

## Contracts and lifecycle

Recover from stored resource/revision identity, not the app's newest revision when history is missing. Active collection
is automatic; collector leases fence ownership across instances. Resolve managedEnvironmentId/workspace scope through
ARM and retain exact app/revision filters for native/legacy tables.

Live discovery validates trusted endpoints, renews short-lived access and bounds deduplication/reconciliation state.
Live HTTP sources run independently of history/metrics so a slow backend does not block every source. Historical
confirmed-tail reads repair gaps; no lossless guarantee is implied.

Reject partial queries and advance cursors only after durable acknowledgement. Event time and ingestion time are
distinct; ingestion/hash ordering is normalized to PostgreSQL microsecond precision without losing original event
timestamps/stable IDs. State/metrics failures produce deduplicated availability, not fabricated values or permission to
monitor another revision.

## Related documentation

- [Parent module](../README.md)
- [Internal collaborators](Runtime/README.md)
- [Queries/tokens](../Monitor/README.md)
- [Cursor models](../../../Domain/Entities/Diagnostics/README.md)
- [Coordinator tests](../../../Test/Infra/Azure/ContainerApps/AzureContainerAppRuntimeStreamerTests.cs)
- [Live stream tests](../../../Test/Infra/Azure/ContainerApps/AzureContainerAppLiveLogsTests.cs)
- [Checkpoint tests](../../../Test/Infra/Azure/ContainerApps/AzureContainerAppLogCheckpointStoreTests.cs)

## Live collector outcome telemetry

ACA discovery, connection, reading and publication use finite operation/termination fields. Shutdown/ownership-refresh
events are Debug, unexpected timeout/transport failures Warning and recovery Information once after failure. Endpoint
inventory removal clears recovery state. No endpoint/replica/provider text becomes an operational metric dimension;
GUID correlation remains in approved logs. Existing reconnect, lease and checkpoint rules remain unchanged.

The connection attempt owns callback operation-stage state, so publication failures retain their originating stage when
an awaited stream callback propagates an exception.

Collector outcomes distinguish provider_conflict (HTTP 409) and provider_rejected (other HTTP response failures)
from transport_failure. They remain warnings, mark the session for a single recovery event and keep existing retry
timing. Provider response text is never inspected for classification.
