# Retention and collection policy schema

Owner-account budget history, permanent deployment history and automatic telemetry collection.

## Contents

| File                                                                                                               | Types or asset                 | Responsibility                                                                    |
|--------------------------------------------------------------------------------------------------------------------|--------------------------------|-----------------------------------------------------------------------------------|
| [20261005190054_AddAiTenantBudgets.cs](20261005190054_AddAiTenantBudgets.cs)                                       | `AddAiTenantBudgets`           | EF schema migration and target-model metadata for Add Ai Tenant Budgets.          |
| [20261005190054_AddAiTenantBudgets.Designer.cs](20261005190054_AddAiTenantBudgets.Designer.cs)                     | `AddAiTenantBudgets`           | EF schema migration and target-model metadata for Add Ai Tenant Budgets.          |
| [20261008202231_PreserveDeploymentHistory.cs](20261008202231_PreserveDeploymentHistory.cs)                         | `PreserveDeploymentHistory`    | EF schema migration and target-model metadata for Preserve Deployment History.    |
| [20261008202231_PreserveDeploymentHistory.Designer.cs](20261008202231_PreserveDeploymentHistory.Designer.cs)       | `PreserveDeploymentHistory`    | EF schema migration and target-model metadata for Preserve Deployment History.    |
| [20261010141504_AutomaticTelemetryCollection.cs](20261010141504_AutomaticTelemetryCollection.cs)                   | `AutomaticTelemetryCollection` | EF schema migration and target-model metadata for Automatic Telemetry Collection. |
| [20261010141504_AutomaticTelemetryCollection.Designer.cs](20261010141504_AutomaticTelemetryCollection.Designer.cs) | `AutomaticTelemetryCollection` | EF schema migration and target-model metadata for Automatic Telemetry Collection. |

## Usage and integration

AddAiTenantBudgets creates metadata-only admissions/attempt reservations and backfills retained quota receipts through
C# project ownership. PreserveDeploymentHistory adds independent completion/snapshot/saved-result state and archive
cleanup work/trigger. AutomaticTelemetryCollection updates compatibility flags/defaults without rewriting stored
diagnostic payloads.

## Contracts and lifecycle

Budget entries cascade only with the owner account, surviving project/result deletion. Backfill cannot reconstruct
already-deleted projects or historical provider charges. Updated workers must participate in locks/reservations; stop
incompatible old binaries before rollout.

PreserveDeploymentHistory retains analyses and backfills only outcomes supported by existing recorded state; stopped
history is not proof of success. The cleanup trigger queues archive deletion even through owner cascades, without
foreign keys to deleted owners. Automatic collection remains separate from AI egress consent.

Review SQL/backfills/Down operations, not only generated model diffs. Preserve migration IDs/designers; this
documentation does not apply a database change.

## Related documentation

- [Parent module](../README.md)
- [Budget semantics](../../Ai/Admission/README.md)
- [Archive cleanup](../../Diagnostics/Archive/README.md)
- [History models](../../../Domain/Entities/Deployments/README.md)
- [Backfill tests](../../../Test/Infra/Ai/Admission/AnalysisBudgetPostgresTests.cs)
- [Retention decision](../../../docs/adr/0004-permanent-deployment-history.md)
