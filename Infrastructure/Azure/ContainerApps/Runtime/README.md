# Azure runtime collaborators

Database passes validate recorded provider composition, use a separate bounded initial lookback and surface source
availability in the database terminal. The coordinator awaits database history even if app history fails, and completes
inventory/metrics before app history. No checkpoint context is shared between the concurrent passes.
Explicitly associated dedicated database apps use their own ready revision and ARM-resolved environment; shared sidecar
CPU/memory remain app totals. See the parent Azure README for the required ownership/component association tags.

These internal types retain the `Infrastructure.Azure` namespace and support the existing host-managed
AzureContainerAppRuntimeStreamer. They do not create detached work or change public provider APIs.

- `AzureDatabaseDiagnostics` runs owned database discovery, metrics and bounded durable log/checkpoint passes.
- `AzureRuntimeAvailability` shares thread-safe outage/recovery deduplication across overlapping polling passes.
- `AzureRuntimeDiagnosticPolicy` maps source identity, stable event hashes and ingestion-order checkpoint comparisons.
- `ContainerAppStreamTarget` holds verified deployment/resource identity and memory-only collection state/credentials.

Checkpoint contexts remain scoped to each pass. Cursors advance only after durable confirmation; collection limits,
provider ordering, native metric units, source isolation and issue counters remain unchanged. Targets and credentials
must never be logged. See [Azure operations](../../README.md) and the runtime/checkpoint regression tests.
