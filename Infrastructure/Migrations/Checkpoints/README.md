# Collector checkpoint schema

GitHub workflow/job progress, Azure source cursors and recoverable cloud collector ownership.

## Contents

| File                                                                                                                           | Types or asset                       | Responsibility                                                                             |
|--------------------------------------------------------------------------------------------------------------------------------|--------------------------------------|--------------------------------------------------------------------------------------------|
| [20260928200050_AddGitHubWorkflowCheckpoints.cs](20260928200050_AddGitHubWorkflowCheckpoints.cs)                               | `AddGitHubWorkflowCheckpoints`       | EF schema migration and target-model metadata for Add Git Hub Workflow Checkpoints.        |
| [20260928200050_AddGitHubWorkflowCheckpoints.Designer.cs](20260928200050_AddGitHubWorkflowCheckpoints.Designer.cs)             | `AddGitHubWorkflowCheckpoints`       | EF schema migration and target-model metadata for Add Git Hub Workflow Checkpoints.        |
| [20260928212104_AddAzureContainerAppLogCheckpoints.cs](20260928212104_AddAzureContainerAppLogCheckpoints.cs)                   | `AddAzureContainerAppLogCheckpoints` | EF schema migration and target-model metadata for Add Azure Container App Log Checkpoints. |
| [20260928212104_AddAzureContainerAppLogCheckpoints.Designer.cs](20260928212104_AddAzureContainerAppLogCheckpoints.Designer.cs) | `AddAzureContainerAppLogCheckpoints` | EF schema migration and target-model metadata for Add Azure Container App Log Checkpoints. |
| [20261004112450_AddCloudTelemetryCollectorLeases.cs](20261004112450_AddCloudTelemetryCollectorLeases.cs)                       | `AddCloudTelemetryCollectorLeases`   | EF schema migration and target-model metadata for Add Cloud Telemetry Collector Leases.    |
| [20261004112450_AddCloudTelemetryCollectorLeases.Designer.cs](20261004112450_AddCloudTelemetryCollectorLeases.Designer.cs)     | `AddCloudTelemetryCollectorLeases`   | EF schema migration and target-model metadata for Add Cloud Telemetry Collector Leases.    |
| [20261011001842_AzureLogIngestionCursors.cs](20261011001842_AzureLogIngestionCursors.cs)                                       | `AzureLogIngestionCursors`           | EF schema migration and target-model metadata for Azure Log Ingestion Cursors.             |
| [20261011001842_AzureLogIngestionCursors.Designer.cs](20261011001842_AzureLogIngestionCursors.Designer.cs)                     | `AzureLogIngestionCursors`           | EF schema migration and target-model metadata for Azure Log Ingestion Cursors.             |

## Usage and integration

GitHub checkpoints establish deployment/run/attempt and per-job state. Azure source checkpoints establish
deployment/source uniqueness. AddCloudTelemetryCollectorLeases adds current collector ownership/expiry on deployments.
AzureLogIngestionCursors adds nullable ingestion timestamp/hash alongside event-time cursors.

## Contracts and lifecycle

Checkpoint rows hold non-sensitive progress/hashes, not raw logs/tokens. Ingestion cursors extend existing rows without
clearing progress or archives; delayed arrivals require ordering distinct from event timestamps. Runtime code advances
only after durable acceptance and preserves PostgreSQL precision.

Folders do not reorder EF migration execution. Preserve historical designer pairs, unique-index semantics and existing
IDs. Nullable legacy fields must remain recoverable without inventing source/revision identities.

## Related documentation

- [Parent module](../README.md)
- [GitHub consumers](../../GitHub/Workflows/README.md)
- [Azure consumers](../../Azure/ContainerApps/README.md)
- [Cursor models](../../../Domain/Entities/Diagnostics/README.md)
- [GitHub tests](../../../Test/Infra/GitHub/GitHubWorkflowCheckpointStoreTests.cs)
- [Azure tests](../../../Test/Infra/Azure/ContainerApps/AzureContainerAppLogCheckpointStoreTests.cs)
