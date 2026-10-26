# Azure Monitor queries

Logs-scoped token exchange, bounded resource/workspace history and native metric aggregation.

## Contents

| File                                                                 | Types or asset                                                                                                | Responsibility                                                                                     |
|----------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------|
| [AzureMetricObservations.cs](AzureMetricObservations.cs)             | `AzureMetricObservation`, `AzureMetricObservations`                                                           | Native minute-bucket observations and provider-dimension aggregation.                              |
| [AzureMonitorLogsClient.cs](AzureMonitorLogsClient.cs)               | `AzureMonitorLogsClient`, `AzureContainerAppLogSource`, `AzureMonitorLogRecord`, `AzureMonitorLogQueryResult` | Bounded resource/workspace log queries, normalized records and finite query failures.              |
| [AzureMonitorLogsOptions.cs](AzureMonitorLogsOptions.cs)             | `AzureMonitorLogsOptions`                                                                                     | OAuth and polling settings for Azure Monitor Logs runtime collection.                              |
| [AzureMonitorLogsTokenProvider.cs](AzureMonitorLogsTokenProvider.cs) | `AzureMonitorLogsTokenProvider`                                                                               | Exchanges the connected user's encrypted refresh token for a short-lived Azure Monitor Logs token. |

## Usage and integration

AzureMonitorLogsTokenProvider exchanges a connected user's protected refresh token for short-lived data-plane access.
AzureMonitorLogsClient normalizes native/legacy app tables and supported database projections. AzureMetricObservations
parses native completed-minute buckets and explicit dimensions. Options are bound/validated by Web.

## Contracts and lifecycle

Defaults: batch 500, at most 32 pages/poll, two-minute overlap, app initial lookback ten minutes, database initial
lookback 24 hours and freshness warning five minutes. The database initial window accepts one minute through seven days
and applies only before confirmed cursor progress; it does not reset existing checkpoints.

Use verified environment/workspace scope plus exact app/revision filters. Legacy empty _ResourceId cases require
ARM-resolved workspace identity. Ingestion-order pages retain event-time identity; partial/error responses do not
advance cursors. Database projections omit query text/documents/parameters/graphs.

Native completed-minute metric timestamps/units remain stable across overlap. Web metrics sum podName dimensions only
within the selected revision; do not convert unavailable samples to zero. Tokens/KQL/resource IDs/records do not belong
in custom telemetry. Access failures return finite safe reasons; cancellation reaches HTTP.

## Related documentation

- [Parent module](../README.md)
- [Coordinator](../ContainerApps/README.md)
- [Query tests](../../../Test/Infra/Azure/Monitor/AzureMonitorLogsClientTests.cs)
- [Token tests](../../../Test/Infra/Azure/Monitor/AzureMonitorLogsTokenProviderTests.cs)
- [Aggregation tests](../../../Test/Infra/Azure/Monitor/AzureMetricObservationsTests.cs)
