# AI analysis persistence

Separate records for results, queued execution, request idempotency, account budgets and automatic failure wakeups.

## Contents

| File                                                                 | Types and responsibility                                                                                                   |
|----------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------|
| [AiDeploymentAnalysis.cs](AiDeploymentAnalysis.cs)                   | AiDeploymentAnalysis: lifecycle, assessment selection/provenance, validated result and optional model/usage/cost metadata. |
| [DeploymentAnalysisWorkItem.cs](DeploymentAnalysisWorkItem.cs)       | DeploymentAnalysisWorkItem: queue identity, fenced lease, recovery attempts and scheduled retries.                         |
| [AiAnalysisRequest.cs](AiAnalysisRequest.cs)                         | AiAnalysisRequest: project-scoped idempotency and quota receipt independent of result lifetime.                            |
| [AiAnalysisBudgetEntry.cs](AiAnalysisBudgetEntry.cs)                 | AiAnalysisBudgetEntry: owner-level admission or provider-attempt reservation accounting.                                   |
| [FailedDeploymentAnalysisEvent.cs](FailedDeploymentAnalysisEvent.cs) | FailedDeploymentAnalysisEvent: one durable failure-admission wakeup/completion marker per deployment.                      |

## Usage and integration

[DeploymentAnalysisService](../../../Infrastructure/Ai/Processing/DeploymentAnalysisService.cs) handles admission,
readback and cancellation. [DeploymentAnalysisQueue](../../../Infrastructure/Ai/Processing/DeploymentAnalysisQueue.cs)
owns claiming and scheduling. [AnalysisBudgetGuard](../../../Infrastructure/Ai/Admission/AnalysisBudgetGuard.cs)
reserves
budgets; [FailedDeploymentAnalysisDispatcher](../../../Infrastructure/Ai/Processing/FailedDeploymentAnalysisDispatcher.cs)
handles automatic admission. Use these workflows rather than independently updating lifecycle fields.

## Contracts and lifecycle

AiDeploymentAnalysis stores validated/redacted guidance and assessment/evidence provenance; it is not a new diagnostic
snapshot store. Optional legacy model/usage/cost metadata can be null. RetainUntilDeleted separates saved-result
retention from finite execution eligibility.

Work items separate lease-recovery AttemptCount from ProviderRetryCount and NextAttemptAt. Cancellation atomically
completes unfinished work and clears leases/retry scheduling in the service; assigning an enum alone does not stop a
worker.

AiAnalysisRequest.ProjectId refers to CsProject. Its receipt survives deployment/result deletion and cascades with that
project. Budget entries instead cascade only with the owner account. ReservedCostUnits use 1/100,000,000 currency units
and represent conservative reservations, not provider invoices. The retention service bounds budget history to ninety
days and cleans expired receipts.

FailedDeploymentAnalysisEvent uses DeploymentId as its primary key; its completed marker survives result cleanup and is
removed with the deployment. Queue, receipt, budget and failure records hold metadata rather than diagnostic payloads.

## Related documentation

- [Entities index](../README.md)
- [Persistence constraints](../../../Infrastructure/Data/AutoMateModelConfiguration.cs)
- [Retention implementation](../../../Infrastructure/Ai/Processing/DeploymentAnalysisRetentionService.cs)
- [AI states](../../Enums/Ai/README.md)
- [Budget tests](../../../Test/Infra/Ai/Admission/AnalysisBudgetTests.cs)
- [Lease tests](../../../Test/Infra/Ai/Processing/AnalysisQueueLeaseTests.cs)
- [AI architecture](../../../docs/ai-analysis.md)
