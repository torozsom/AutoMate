# Azure runtime collaborators

Per-target database collection, source availability and stable event/checkpoint policy.

## Contents

| File                                                               | Types or asset                 | Responsibility                                                                                                 |
|--------------------------------------------------------------------|--------------------------------|----------------------------------------------------------------------------------------------------------------|
| [AzureDatabaseDiagnostics.cs](AzureDatabaseDiagnostics.cs)         | `AzureDatabaseDiagnostics`     | Collects owned managed-database metrics and durable log checkpoints separately from web runtime supervision.   |
| [AzureRuntimeAvailability.cs](AzureRuntimeAvailability.cs)         | `AzureRuntimeAvailability`     | Owns deployment/source outage deduplication and recovery counters for one runtime coordinator.                 |
| [AzureRuntimeDiagnosticPolicy.cs](AzureRuntimeDiagnosticPolicy.cs) | `AzureRuntimeDiagnosticPolicy` | Maps Azure source identity, event hashes and checkpoint ordering independently of polling and scope lifetimes. |
| [ContainerAppStreamTarget.cs](ContainerAppStreamTarget.cs)         | `ContainerAppStreamTarget`     | Holds verified resource identity, collector cursors and current runtime observations.                          |

## Usage and integration

AzureDatabaseDiagnostics discovers owned databases, completes inventory/metrics before app history and runs database
history in separate scopes. AzureRuntimeAvailability deduplicates outages/recovery. AzureRuntimeDiagnosticPolicy maps
identity, event hashes and ingestion ordering. ContainerAppStreamTarget holds verified identities, cursors, observations
and memory-only credentials.

## Contracts and lifecycle

Keep targets/credentials out of logs. Each history pass owns its checkpoint context; concurrent app/database work must
not share a DbContext. A failed app-history query must not starve database history. Confirm durability before cursor
updates and preserve delayed-ingestion ordering.

Recorded provider composition constrains inventory; legacy cases still require ownership tags. Dedicated database apps
use their own ready revision/environment and exact component association. Shared sidecar CPU/memory remains app totals.
Availability notices route to the database terminal when database-specific; quiet/metrics-only sources are not
equivalent to permission failure.

## Related documentation

- [Parent module](../README.md)
- [Database associations](../../Databases/README.md)
- [Lookback/query behavior](../../Monitor/README.md)
- [Identity/cursor tests](../../../../Test/Infra/Azure/ContainerApps/AzureRuntimeDiagnosticPolicyTests.cs)
- [Source isolation tests](../../../../Test/Infra/Azure/ContainerApps/AzureContainerAppRuntimeStreamerTests.cs)
