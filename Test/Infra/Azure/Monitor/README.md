# Azure Monitor and database tests

Query scoping/parsing, native timestamps/dimensions, token guidance and owned database inventory.

## Contents

| File                                                                           | Test or fixture types                | Responsibility                                                                                                 |
|--------------------------------------------------------------------------------|--------------------------------------|----------------------------------------------------------------------------------------------------------------|
| [AzureManagedDatabaseClientTests.cs](AzureManagedDatabaseClientTests.cs)       | `AzureManagedDatabaseClientTests`    | Guards database discovery against collecting unrelated resources in a shared group.                            |
| [AzureMetricObservationsTests.cs](AzureMetricObservationsTests.cs)             | `AzureMetricObservationsTests`       | Verifies native timestamps, dimension aggregation, gaps and Cosmos units.                                      |
| [AzureMonitorLogsClientTests.cs](AzureMonitorLogsClientTests.cs)               | `AzureMonitorLogsClientTests`        | Verifies Azure diagnostic query scoping, parsing and ingestion-cursor behavior using synthetic HTTP responses. |
| [AzureMonitorLogsTokenProviderTests.cs](AzureMonitorLogsTokenProviderTests.cs) | `AzureMonitorLogsTokenProviderTests` | Verifies fixed consent guidance without exposing provider response messages.                                   |

## Coverage and navigation

LogsClient tests resource/workspace scope, event versus ingestion time/hash ordering and partial-error rejection.
TokenProvider tests fixed safe denial guidance. MetricObservations tests per-minute replica sums and maximum native
Cosmos RU. ManagedDatabaseClient tests association/provider tags, SQL child paging, trusted continuations and Redis
units.

## Fixtures and boundaries

Synthetic HTTP responses contain all provider data; no Azure credentials are required. Assert absent CPU/memory/metric
samples remain unavailable rather than zero. Malicious continuation/resource identity must never widen collection.
Fixtures exercise parsing/permission classification, not live backend ingestion delay or diagnostic-setting
installation.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra.Azure.Monitor
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Queries/tokens](../../../../Infrastructure/Azure/Monitor/README.md)
- [Inventory](../../../../Infrastructure/Azure/Databases/README.md)
- [HTTP fixtures](../../TestSupport/README.md)
