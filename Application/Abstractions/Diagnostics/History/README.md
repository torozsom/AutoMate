# Archive and replay contracts

Tenant-scoped deployment archives, bounded log replay, assessment evidence and versioned history cursors.

## Contents

| File                                                               | Types                                                                                       | Responsibility                                                                                                |
|--------------------------------------------------------------------|---------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------|
| [IDeploymentArchive.cs](IDeploymentArchive.cs)                     | `ArchiveMetricRequest`, `ArchiveDeleteRequest`, `ArchiveMetricImport`, `IDeploymentArchive` | Permanent archive append/read/import/delete and metric request shapes, including compatibility defaults.      |
| [IDeploymentAssessmentArchive.cs](IDeploymentAssessmentArchive.cs) | `IDeploymentAssessmentArchive`                                                              | Guarantees filtered archive assessment reads independently of the wider legacy archive compatibility surface. |
| [IDeploymentDiagnosticStore.cs](IDeploymentDiagnosticStore.cs)     | `IDeploymentDiagnosticStore`                                                                | Reads and expires redacted legacy diagnostics and builds bounded compatibility context.                       |
| [TelemetryHistoryV2.cs](TelemetryHistoryV2.cs)                     | `TelemetryLogPage`, `TelemetryHistoryCursor`, `IDeploymentLogSearch`                        | Versioned scoped cursors, replay page shape and bounded log-search capability.                                |

## Usage and integration

IDeploymentArchive defines durable append, ordered reads, metrics, import and project deletion.
IDeploymentAssessmentArchive is the focused capability for selection before bounded evidence/metric reads.
IDeploymentDiagnosticStore retains legacy redacted persistence/context APIs. TelemetryHistoryV2 supplies
TelemetryLogPage, scoped cursor encoding and IDeploymentLogSearch.

## Contracts and lifecycle

TelemetryHistoryCursor encodes version/project/deployment/order; null/empty means initial position and
malformed/mismatched/oversized cursors throw ArgumentException. Cursor binding is not owner authentication: public read
services must authorize the owner before archive access. Treat cursors as opaque; restart traversal when channel
changes.

Archive/log-search channel filtering must precede page limits. Preserve availability and cursor-advance flags for
pending/unavailable storage. The focused assessment capability guarantees pre-limit selection; legacy archive defaults
may throw, filter metric results after a bounded read or return explicit detailed-metrics unavailability. Do not
silently treat these defaults as full backend support.

BuildContextAsync remains legacy text without a selected evidence catalog. New AI execution uses the
context-builder/assessment ports. Current new payloads use the telemetry gateway/disk archive; the old persistence
interface is not a recommendation to add PostgreSQL diagnostic writes. Deletion and import require trusted
tenant/project scope.

## Related documentation

- [Parent module](../README.md)
- [Disk archive](../../../../Infrastructure/Diagnostics/Archive/DiskDeploymentArchive.cs)
- [Private-host archive adapter](../../../../Infrastructure/Diagnostics/Delivery/TelemetryGatewayClient.cs)
- [Owner-facing replay](../../../../Infrastructure/Diagnostics/Archive/DeploymentHistoryService.cs)
- [Assessment selection queries](../../Ai/Context/README.md)
- [Archive tests](../../../../Test/Infra/Diagnostics/Archive/DiskDeploymentArchiveTests.cs)
- [Replay tests](../../../../Test/Infra/Diagnostics/Archive/DeploymentDiagnosticReplayTests.cs)
