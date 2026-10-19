# Analysis execution ports

Owner-facing analysis management, detached durable queue claims and read-only readiness reporting.

## Contents

| File                                                               | Types                                                                                                                                                                 | Responsibility                                                                                                      |
|--------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------|
| [IDeploymentAnalysisQueue.cs](IDeploymentAnalysisQueue.cs)         | `DeploymentAnalysisWorkItem`, `IDeploymentAnalysisQueue`                                                                                                              | Detached claim metadata; the lease token fences this attempt, and no diagnostic context is queued.                  |
| [IDeploymentAnalysisReadiness.cs](IDeploymentAnalysisReadiness.cs) | `IDeploymentAnalysisReadiness`, `AnalysisConfigurationState`, `DeploymentAnalysisReadiness`                                                                           | Reports local AI configuration and durable metadata access without claiming work or contacting a provider.          |
| [IDeploymentAnalysisService.cs](IDeploymentAnalysisService.cs)     | `DeploymentAnalysisView`, `DeploymentAnalysisRequestResult`, `DeploymentAnalysisDeletionResult`, `DeploymentAnalysisCancellationResult`, `IDeploymentAnalysisService` | Owner-facing admission/readback/preferences/cancellation/deletion, safe result views and finite operation outcomes. |

## Usage and integration

IDeploymentAnalysisService admits manual requests, manages per-deployment preferences, reads/lists results and
cancels/deletes analyses. IDeploymentAnalysisQueue atomically claims work and renews/releases current leases.
IDeploymentAnalysisReadiness reports finite configuration state plus queue availability without invoking a provider.

## Contracts and lifecycle

DeploymentAnalysisWorkItem here is detached claim metadata, distinct from the Domain entity of the same name. LeaseId
fences an acquisition; false renewal means stop. Attempt counts acquisitions since the last scheduled retry;
ProviderRetryCount tracks transient retries separately. Queue no diagnostic context.

Stable GUID request IDs are scoped by owner/deployment. Within the ninety-day receipt window, live results replay and
deleted/expired results are not recreated; different normalized options under the same ID conflict. Legacy admission
coalesces active work. Current policy/consent still apply to replay. Accepted=false may carry a safe Skipped view for
quota/egress denials, so inspect both fields.

CancelAsync is idempotent for Cancelled, returns AlreadyFinished for completed/failed/skipped, and NotFound for
missing/foreign/expired scope. It works while AI is disabled and retires active/retry work atomically in the
implementation. DeleteAsync separately removes terminal/expired results and may return InProgress. Readback does not
call a provider; unknown provenance stays null.

Compatibility defaults can ignore selection, return null/false preferences or list only the latest item. The
Infrastructure service overrides these for full behavior. Readiness neither claims work nor loads context nor grants
consent; Web maps it to /health/ready independently of liveness.

## Related documentation

- [Parent module](../README.md)
- [Admission and result management](../../../../Infrastructure/Ai/Processing/DeploymentAnalysisService.cs)
- [Durable claims](../../../../Infrastructure/Ai/Processing/DeploymentAnalysisQueue.cs)
- [Readiness checks](../../../../Infrastructure/Ai/Admission/DeploymentAnalysisReadinessService.cs)
- [Persistent metadata](../../../../Domain/Entities/Ai/README.md)
- [Admission tests](../../../../Test/Infra/Ai/Admission/AnalysisAdmissionTests.cs)
- [Lease tests](../../../../Test/Infra/Ai/Processing/AnalysisQueueLeaseTests.cs)
- [Readiness tests](../../../../Test/Infra/Ai/Admission/AnalysisReadinessTests.cs)
