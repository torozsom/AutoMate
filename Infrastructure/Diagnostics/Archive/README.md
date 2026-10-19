# Permanent deployment archives

Checksummed retained history, owner-authorized replay, metric lookup, bounded backfill and deletion outbox processing.

## Contents

| File                                                                     | Types or asset                                              | Responsibility                                                                                   |
|--------------------------------------------------------------------------|-------------------------------------------------------------|--------------------------------------------------------------------------------------------------|
| [ArchiveMetricBatchReader.cs](ArchiveMetricBatchReader.cs)               | `ArchiveMetricBatchReader`                                  | Resolves private batch scopes in SQL; callers cannot supply an unauthorized partition list.      |
| [DeploymentArchiveBackfillWorker.cs](DeploymentArchiveBackfillWorker.cs) | `DeploymentArchiveBackfillWorker`, `Progress`, `Checkpoint` | Imports bounded pages of still-retained diagnostics, with crash-safe per-deployment progress.    |
| [DeploymentArchiveCleanupWorker.cs](DeploymentArchiveCleanupWorker.cs)   | `DeploymentArchiveCleanupWorker`                            | Retries deletion of archived payloads after transactional metadata deletion.                     |
| [DeploymentHistoryService.cs](DeploymentHistoryService.cs)               | `DeploymentHistoryService`                                  | Authorizes deployment history before any provider query or preference change.                    |
| [DiskDeploymentArchive.cs](DiskDeploymentArchive.cs)                     | `DiskDeploymentArchive`, `Segment`, `Aggregate`             | Checksummed immutable per-event segments on the telemetry volume, retained until owner deletion. |
| [DiskDeploymentArchive.Metrics.cs](DiskDeploymentArchive.Metrics.cs)     | `DiskDeploymentArchive`                                     | Rebuildable numeric lookup; immutable checksummed archive segments remain authoritative.         |

## Usage and integration

DiskDeploymentArchive implements archive/assessment ports over immutable per-event segments on the private telemetry
volume. Its Metrics partial maintains rebuildable numeric lookup. DeploymentHistoryService authorizes public
history/preferences. ArchiveMetricBatchReader resolves private owner/project scope in SQL. BackfillWorker imports
still-retained sources; CleanupWorker retries transactional deletion work.

## Contracts and lifecycle

Permanent archive data remains authoritative until project deletion; indexes are rebuildable, not alternate retention
stores. Append rechecks redaction and stable event identity. Filter channels/assessment sources before limits.
Cursor/availability flags must disclose omissions and delay.

Backfill uses bounded pages and checksummed progress; repeated/restarted work must not duplicate acknowledged events. It
cannot recreate already-expired source data. Cleanup outbox survives deleted owners and removes archived partitions
asynchronously.

Public history services authorize exact owner/project/deployment before backend access. Private trusted tenant IDs are
not a substitute for this check. Missing/incompatible hosts return safe availability rather than successful empty
history. Chart/metric batch reads preserve frozen scopes and sufficient statistics.

## Related documentation

- [Parent module](../README.md)
- [Archive/replay contracts](../../../Application/Abstractions/Diagnostics/History/README.md)
- [Private gateway](../Delivery/README.md)
- [Archive tests](../../../Test/Infra/Diagnostics/Archive/DiskDeploymentArchiveTests.cs)
- [Replay tests](../../../Test/Infra/Diagnostics/Archive/DeploymentDiagnosticReplayTests.cs)
- [Retention and rollout](../../../docs/adr/0004-permanent-deployment-history.md)
