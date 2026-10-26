# Diagnostic persistence and cursors

Durable collector progress, legacy diagnostic storage/delivery metadata and retained daily telemetry statistics.

## Contents

| File                                                                   | Types and responsibility                                                                                |
|------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------|
| [AzureContainerAppLogCheckpoint.cs](AzureContainerAppLogCheckpoint.cs) | AzureContainerAppLogCheckpoint: event/ingestion cursors and ordering hashes for each deployment/source. |
| [GitHubWorkflowCheckpoint.cs](GitHubWorkflowCheckpoint.cs)             | GitHubWorkflowCheckpoint: workflow-attempt fingerprint, final reconciliation and job cursors.           |
| [GitHubWorkflowJobCheckpoint.cs](GitHubWorkflowJobCheckpoint.cs)       | GitHubWorkflowJobCheckpoint: state/log hashes, published-line count and availability/finality metadata. |
| [DeploymentDailyTelemetry.cs](DeploymentDailyTelemetry.cs)             | DeploymentDailyTelemetry: scoped UTC-day sufficient statistics, observed errors and completeness flag.  |
| [DeploymentDiagnosticRecord.cs](DeploymentDiagnosticRecord.cs)         | DeploymentDiagnosticRecord: legacy redacted history and retained delivery-buffer metadata.              |
| [DeploymentDiagnosticSnapshot.cs](DeploymentDiagnosticSnapshot.cs)     | DeploymentDiagnosticSnapshot: legacy redacted analysis context and expiry.                              |
| [TelemetryTenantState.cs](TelemetryTenantState.cs)                     | TelemetryTenantState: delivery lease, ordering, rate/byte budgets, loss and metric-series accounting.   |

## Usage and integration

[GitHubWorkflowCheckpointStore](../../../Infrastructure/GitHub/Workflows/GitHubWorkflowCheckpointStore.cs) maintains
workflow/job
progress. [AzureContainerAppLogCheckpointStore](../../../Infrastructure/Azure/ContainerApps/AzureContainerAppLogCheckpointStore.cs)
maintains Azure cursors. Follow [Diagnostics implementations](../../../Infrastructure/Diagnostics/README.md) for legacy
reads/delivery and the current gateway path.

## Contracts and lifecycle

Workflow checkpoints are scoped to deployment/run/attempt, with per-job hashes and line counts. They retain progress
rather than raw workflow logs. Azure checkpoints separate event-time and ingestion-time cursors for delayed arrivals;
Source can be console, system or a bounded db-resource hash. Preserve existing cursors when extending ingestion
tracking.

DeploymentDiagnosticRecord and DeploymentDiagnosticSnapshot support legacy history/context and retained buffering. New
log/metric payloads use the private telemetry gateway; these models do not authorize new persisted analysis context.
Redaction and retention are enforced outside the entities.

TelemetryTenantState holds delivery leases, rate windows and loss/byte/series accounting; the empty-tenant row
represents global buffered-byte accounting. DeploymentDailyTelemetry stores count/sum/min/max in native units. Use
count-weighted averages and retain Incomplete gaps. ObservedErrors is an observed count, not a service-wide error rate.

## Related documentation

- [Entities index](../README.md)
- [Mappings](../../../Infrastructure/Data/AutoMateModelConfiguration.cs)
- [Deployment ownership](../Deployments/README.md)
- [GitHub cursor tests](../../../Test/Infra/GitHub/GitHubWorkflowCheckpointStoreTests.cs)
- [Azure cursor tests](../../../Test/Infra/Azure/ContainerApps/AzureContainerAppLogCheckpointStoreTests.cs)
- [Telemetry tests](../../../Test/Infra/Diagnostics/Archive/SaaSTelemetryTests.cs)
- [Telemetry operations](../../../docs/deployment-telemetry.md)
