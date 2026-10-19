# Telemetry metadata evolution

Durable replay ordering, legacy specialized delivery/accounting and retained daily analytics.

## Contents

| File                                                                                                                   | Types or asset                   | Responsibility                                                                       |
|------------------------------------------------------------------------------------------------------------------------|----------------------------------|--------------------------------------------------------------------------------------|
| [20261002144610_AddTerminalLogReplay.cs](20261002144610_AddTerminalLogReplay.cs)                                       | `AddTerminalLogReplay`           | EF schema migration and target-model metadata for Add Terminal Log Replay.           |
| [20261002144610_AddTerminalLogReplay.Designer.cs](20261002144610_AddTerminalLogReplay.Designer.cs)                     | `AddTerminalLogReplay`           | EF schema migration and target-model metadata for Add Terminal Log Replay.           |
| [20261003161839_AddSpecializedTelemetryStorage.cs](20261003161839_AddSpecializedTelemetryStorage.cs)                   | `AddSpecializedTelemetryStorage` | EF schema migration and target-model metadata for Add Specialized Telemetry Storage. |
| [20261003161839_AddSpecializedTelemetryStorage.Designer.cs](20261003161839_AddSpecializedTelemetryStorage.Designer.cs) | `AddSpecializedTelemetryStorage` | EF schema migration and target-model metadata for Add Specialized Telemetry Storage. |
| [20261003170209_AddTelemetryBufferAccounting.cs](20261003170209_AddTelemetryBufferAccounting.cs)                       | `AddTelemetryBufferAccounting`   | EF schema migration and target-model metadata for Add Telemetry Buffer Accounting.   |
| [20261003170209_AddTelemetryBufferAccounting.Designer.cs](20261003170209_AddTelemetryBufferAccounting.Designer.cs)     | `AddTelemetryBufferAccounting`   | EF schema migration and target-model metadata for Add Telemetry Buffer Accounting.   |
| [20261004111715_AddSaaSTelemetryAnalytics.cs](20261004111715_AddSaaSTelemetryAnalytics.cs)                             | `AddSaaSTelemetryAnalytics`      | EF schema migration and target-model metadata for Add Saa STelemetry Analytics.      |
| [20261004111715_AddSaaSTelemetryAnalytics.Designer.cs](20261004111715_AddSaaSTelemetryAnalytics.Designer.cs)           | `AddSaaSTelemetryAnalytics`      | EF schema migration and target-model metadata for Add Saa STelemetry Analytics.      |

## Usage and integration

AddTerminalLogReplay establishes ordering/channel replay metadata and backfills existing rows.
AddSpecializedTelemetryStorage adds legacy preference/delivery fields and tenant leases/rate/series state.
AddTelemetryBufferAccounting backfills tenant/global buffered byte accounting. AddSaaSTelemetryAnalytics adds deployment
runtime identities and daily sufficient statistics.

## Contracts and lifecycle

These are historical schemas: current hosts reject new PostgreSQL payload writes and use the disk gateway, while
retaining legacy replay/drain support. Keep migrations for upgrading existing databases; do not remove tables/columns
merely because the current write path changed.

Positive stable order/cursors preserve replay; buffered accounting differs from permanent archives and daily summaries.
Later policy/history migrations supersede legacy preference/retention interpretation. Preserve global migration order,
raw SQL identity/backfills and paired designers.

## Related documentation

- [Parent module](../README.md)
- [Compatibility reads/drain](../../Diagnostics/Storage/README.md)
- [Daily analytics](../../Diagnostics/Metrics/README.md)
- [Models](../../../Domain/Entities/Diagnostics/README.md)
- [Replay tests](../../../Test/Infra/Diagnostics/Archive/DeploymentDiagnosticReplayTests.cs)
- [Analytics tests](../../../Test/Infra/Diagnostics/Archive/SaaSTelemetryTests.cs)
