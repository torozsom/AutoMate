# AI lifecycle classifications

States and admission origins for asynchronous deployment analysis.

## Contents

| File                                         | Types and responsibility                                                     |
|----------------------------------------------|------------------------------------------------------------------------------|
| [AiAnalysisStatus.cs](AiAnalysisStatus.cs)   | AiAnalysisStatus: Queued, Running, Completed, Failed, Skipped and Cancelled. |
| [AiAnalysisTrigger.cs](AiAnalysisTrigger.cs) | AiAnalysisTrigger: DeploymentFailed or Manual admission origin.              |

## Usage and integration

Used
by [AI persistence](../../Entities/Ai/README.md), [DeploymentAnalysisService](../../../Infrastructure/Ai/Processing/DeploymentAnalysisService.cs)
and [DeploymentAnalysisQueue](../../../Infrastructure/Ai/Processing/DeploymentAnalysisQueue.cs). Policy/queue code
determines eligibility; request and cancel work through those workflows.

## Contracts and compatibility

AiAnalysisStatus explicitly preserves Queued = 0, Running = 1, Completed = 2, Failed = 3, Skipped = 4 and Cancelled = 5.
Queued can mean initial work or a scheduled retry. Completed/Failed/Skipped/Cancelled are terminal outcomes.
Cancellation prevents later publication through fenced service/queue operations, not through enum assignment alone.

AiAnalysisTrigger distinguishes automatic deployment-failure admission from manual requests. Neither origin bypasses
admission policy or AI egress consent. Keep persisted values stable when extending these enums.

## Related documentation

- [Enums index](../README.md)
- [Queue lifecycle tests](../../../Test/Infra/Ai/Processing/AnalysisQueueLeaseTests.cs)
- [Admission tests](../../../Test/Infra/Ai/Admission/AnalysisAdmissionTests.cs)
