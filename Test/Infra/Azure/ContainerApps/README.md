# Container Apps tests

Synthetic runtime state/metrics, exact-revision streams, durable cursors and source availability.

## Contents

| File                                                                                       | Test or fixture types                                                                    | Responsibility                                                                                                    |
|--------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------|
| [AzureContainerAppClientTests.cs](AzureContainerAppClientTests.cs)                         | `AzureContainerAppClientTests`                                                           | Verifies Azure Monitor names and conversion to the shared metric units.                                           |
| [AzureContainerAppLiveLogsTests.cs](AzureContainerAppLiveLogsTests.cs)                     | `AzureContainerAppLiveLogsTests`, `RecordingPublisher`                                   | Exercises incremental ACA HTTP reading, ownership boundaries and stream supervision without Azure credentials.    |
| [AzureContainerAppLogCheckpointStoreTests.cs](AzureContainerAppLogCheckpointStoreTests.cs) | `AzureContainerAppLogCheckpointStoreTests`                                               | Verifies durable Azure log cursor identity and checkpoint persistence.                                            |
| [AzureContainerAppRuntimeStreamerTests.cs](AzureContainerAppRuntimeStreamerTests.cs)       | `AzureContainerAppRuntimeStreamerTests`, `RecordingPublisher`, `SuccessfulTokenProvider` | Verifies isolated Azure runtime source collection and durable acknowledgement using synthetic provider responses. |
| [AzureRuntimeDiagnosticPolicyTests.cs](AzureRuntimeDiagnosticPolicyTests.cs)               | `AzureRuntimeDiagnosticPolicyTests`, `Publisher`                                         | Protects database terminal routing and source-specific availability reporting.                                    |

## Coverage and navigation

Client tests verify ARM/environment/workspace scope and nanocore/unit conversion with missing samples retained as gaps.
LiveLogs tests exercise incremental delivery before EOF, cancellation, trusted endpoints, replica discovery,
access-denied guidance, reconnection and expired ownership. RuntimeStreamer/Policy/Checkpoint tests cover delayed
ingestion, isolated revisions, database association and acknowledgement ordering.

## Fixtures and boundaries

Controlled HTTP streams and owned local metadata replace Azure. Confirm only accepted durable records advance cursors;
rejected/partial work must survive restart. Keep app/system/database sources distinct and native metric meanings intact.
These tests do not enable actual Azure exports or prove connected-account permissions.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra.Azure.ContainerApps
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Coordinator/streams](../../../../Infrastructure/Azure/ContainerApps/README.md)
- [Source policy](../../../../Infrastructure/Azure/ContainerApps/Runtime/README.md)
- [Cursor models](../../../../Domain/Entities/Diagnostics/README.md)
