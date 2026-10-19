# Analysis queue schema

Renewable claims, transient retry scheduling, idempotency receipts and database failure wakeups.

## Contents

| File                                                                                                                         | Types or asset                      | Responsibility                                                                           |
|------------------------------------------------------------------------------------------------------------------------------|-------------------------------------|------------------------------------------------------------------------------------------|
| [20261005091947_AddAnalysisQueueLeases.cs](20261005091947_AddAnalysisQueueLeases.cs)                                         | `AddAnalysisQueueLeases`            | EF schema migration and target-model metadata for Add Analysis Queue Leases.             |
| [20261005091947_AddAnalysisQueueLeases.Designer.cs](20261005091947_AddAnalysisQueueLeases.Designer.cs)                       | `AddAnalysisQueueLeases`            | EF schema migration and target-model metadata for Add Analysis Queue Leases.             |
| [20261005094754_AddAnalysisRetryScheduling.cs](20261005094754_AddAnalysisRetryScheduling.cs)                                 | `AddAnalysisRetryScheduling`        | EF schema migration and target-model metadata for Add Analysis Retry Scheduling.         |
| [20261005094754_AddAnalysisRetryScheduling.Designer.cs](20261005094754_AddAnalysisRetryScheduling.Designer.cs)               | `AddAnalysisRetryScheduling`        | EF schema migration and target-model metadata for Add Analysis Retry Scheduling.         |
| [20261005102239_AddAnalysisAdmissionReceipts.cs](20261005102239_AddAnalysisAdmissionReceipts.cs)                             | `AddAnalysisAdmissionReceipts`      | EF schema migration and target-model metadata for Add Analysis Admission Receipts.       |
| [20261005102239_AddAnalysisAdmissionReceipts.Designer.cs](20261005102239_AddAnalysisAdmissionReceipts.Designer.cs)           | `AddAnalysisAdmissionReceipts`      | EF schema migration and target-model metadata for Add Analysis Admission Receipts.       |
| [20261005104851_AddFailedDeploymentAnalysisEvents.cs](20261005104851_AddFailedDeploymentAnalysisEvents.cs)                   | `AddFailedDeploymentAnalysisEvents` | EF schema migration and target-model metadata for Add Failed Deployment Analysis Events. |
| [20261005104851_AddFailedDeploymentAnalysisEvents.Designer.cs](20261005104851_AddFailedDeploymentAnalysisEvents.Designer.cs) | `AddFailedDeploymentAnalysisEvents` | EF schema migration and target-model metadata for Add Failed Deployment Analysis Events. |

## Usage and integration

AddAnalysisQueueLeases adds lease token/expiry/recovery-attempt metadata and eligibility indexing.
AddAnalysisRetryScheduling adds future eligibility and a separate provider retry count. AddAnalysisAdmissionReceipts
introduces stable request/quota history with its data backfill. AddFailedDeploymentAnalysisEvents creates
deployment-keyed wakeups and installs failure-capture SQL.

## Contracts and lifecycle

Stop incompatible older workers before queue/receipt/budget upgrades; older binaries do not enforce the newer
leases/scheduling. No prompt/context payload belongs in these tables. Receipt lifetime is separate from deletable
results; failure marker completion remains until deployment deletion.

Preserve paired designer identities and review Up/Down SQL plus backfills/trigger operations. EF executes global
timestamp order. Runtime queue fencing, not the presence of columns, prevents stale publication; tests must cover actual
database behavior.

## Related documentation

- [Parent module](../README.md)
- [Queue consumers](../../Ai/Processing/README.md)
- [Capture SQL](../../Data/FailedDeploymentAnalysisTrigger.cs)
- [Models](../../../Domain/Entities/Ai/README.md)
- [Lease tests](../../../Test/Infra/Ai/Processing/AnalysisQueueLeaseTests.cs)
- [Trigger tests](../../../Test/Infra/Ai/Processing/FailedDeploymentTriggerPostgresTests.cs)
- [Upgrade order](../../../docs/ai-analysis.md)
