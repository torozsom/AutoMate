# Durable analysis processing

Owner-authorized result management, fenced execution, independent retry/recovery budgets, failure wakeups and retention.

## Contents

| File                                                                           | Types or asset                                                                                      | Responsibility                                                                                                            |
|--------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------|
| [DeploymentAnalysisProcessor.cs](DeploymentAnalysisProcessor.cs)               | `DeploymentAnalysisProcessor`                                                                       | Executes one leased analysis with explicit dependencies; deferred boundaries preserve admission-before-provider ordering. |
| [DeploymentAnalysisQueue.cs](DeploymentAnalysisQueue.cs)                       | `DeploymentAnalysisQueue`                                                                           | Uses conditional database updates for multi-instance ownership and renewable crash-recovery leases.                       |
| [DeploymentAnalysisRetentionService.cs](DeploymentAnalysisRetentionService.cs) | `DeploymentAnalysisRetentionService`                                                                | Reclaims expired analysis metadata and cascading work items even when AI admission is disabled.                           |
| [DeploymentAnalysisService.cs](DeploymentAnalysisService.cs)                   | `DeploymentAnalysisService`                                                                         | Owner-authorized analysis admission and redacted readback, including legacy-result validation.                            |
| [DeploymentAnalysisWorker.cs](DeploymentAnalysisWorker.cs)                     | `DeploymentAnalysisWorker`, `AnalysisRecoveryExhaustedException`, `AnalysisRetryExhaustedException` | Processes queued diagnoses in bounded independent scopes; every slot retains lease renewal and result fencing.            |
| [FailedDeploymentAnalysisDispatcher.cs](FailedDeploymentAnalysisDispatcher.cs) | `FailedDeploymentAnalysisDispatcher`                                                                | Recovers transactional failure wakeups without using UI notifications or diagnostic payload storage.                      |

## Usage and integration

DeploymentAnalysisService owns admission/receipts, preferences, readback, cancellation and deletion.
DeploymentAnalysisQueue atomically claims/renews/releases work. DeploymentAnalysisWorker owns scoped slots and renewal;
DeploymentAnalysisProcessor executes one attempt with deferred dependencies. FailedDeploymentAnalysisDispatcher recovers
database failure markers. Retention runs independently of feature enablement.

## Contracts and lifecycle

Stable request IDs scope to owner/deployment and ninety-day receipts; conflicting canonical options return Conflict.
Deleted/expired results are not recreated by replay in that window. Current policy still applies. Safe Skipped denials
can return without work or quota consumption.

Processing verifies current lease, egress, bounded ephemeral context and reserved spend before provider resolution.
Conditional writes fence publication/retries; stale completions are discarded. Recovery acquisitions and scheduled
provider retries have separate bounds; retries rebuild context.

Cancellation atomically retires work/leases/future retries and prevents later publication, including while execution is
disabled. Deletion separately authorizes owner/deployment/analysis and cascades work metadata. Legacy readback
revalidates safe fields without inventing evidence membership.

RetainUntilDeleted results outlive finite execution expiry; expired active retained work becomes Cancelled. Other
expired results/receipts and budget entries older than ninety days are cleaned in bounded batches. Failure markers
survive result cleanup until deployment deletion. Queue/wakeup rows contain metadata, not context.

## Related documentation

- [Parent module](../README.md)
- [Execution details](../../../Application/Abstractions/Ai/Execution/README.md)
- [Persistence lifetimes](../../../Domain/Entities/Ai/README.md)
- [Current gates](../Admission/README.md)
- [Lease/retry tests](../../../Test/Infra/Ai/Processing/AnalysisQueueLeaseTests.cs)
- [Concurrency tests](../../../Test/Infra/Ai/Processing/AnalysisWorkerConcurrencyTests.cs)
- [Failure capture](../../../Test/Infra/Ai/Processing/FailedDeploymentTriggerPostgresTests.cs)
- [Admission tests](../../../Test/Infra/Ai/Admission/AnalysisAdmissionTests.cs)
