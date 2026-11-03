# Deployment orchestration tests

Resource-aware scheduling, owned scopes, local supervision, cloud admission and startup reconciliation.

## Contents

| File                                                                             | Test or fixture types                                                                                                                    | Responsibility                                                                            |
|----------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------|
| [CloudDeploymentRunServiceTests.cs](CloudDeploymentRunServiceTests.cs)           | `CloudDeploymentRunServiceTests`, `AdmissionClock`, `Fixture`, `StubGitHubApp`                                                           | Verifies SaaS admission authorization, idempotency, quota, and encrypted snapshots.       |
| [CloudFailurePrivacyTests.cs](CloudFailurePrivacyTests.cs)                       | `CloudFailurePrivacyTests`                                                                                                               | Checks the customer-visible failure policy against untrusted exception suffixes.          |
| [DeploymentCleanupHostedServiceTests.cs](DeploymentCleanupHostedServiceTests.cs) | `DeploymentCleanupHostedServiceTests`, `EmptyDocker`                                                                                     | Docker startup reconciliation never changes remote Azure deployment status.               |
| [DeploymentJobWorkerTests.cs](DeploymentJobWorkerTests.cs)                       | `DeploymentJobWorkerTests`, `DelayedReadQueue`, `DelayedReader`, `Harness`, `Start`, `Tracker`, `FakeLocal`, `FakeCloud`, `Capabilities` | Verifies queued deployment and stop isolation, scope ownership and shutdown behavior.     |
| [LocalDiagnosticSupervisionTests.cs](LocalDiagnosticSupervisionTests.cs)         | `LocalDiagnosticSupervisionTests`, `Harness`, `PolicyContext`, `TestSource`, `Subscription`                                              | Verifies host ownership, replacement and consent using real isolated SQLite policy reads. |
| [LocalHostPortAvailabilityTests.cs](LocalHostPortAvailabilityTests.cs)           | `LocalHostPortAvailabilityTests`                                                                                                         | Verifies bound-port detection before local Docker deployment starts.                      |

## Coverage and navigation

DeploymentJobWorkerTests verifies independent lanes/stops, caps/defaults, project/port/branch ordering, queue bounds,
failure isolation and shutdown reads/scopes. LocalDiagnosticSupervisionTests verifies cancellation/replacement outside
registry locks and automatic sources. Port tests reserve an actual local socket before launch. Cloud run tests verify
clock/idempotency/quota/protected snapshots; cleanup/privacy tests protect Azure state and fixed guidance.

## Fixtures and boundaries

Use SQLite/controlled orchestrators/sources and ephemeral local sockets; no real Docker/cloud launch is required.
Preserve per-project ordering without blocking unrelated projects or occupying lanes for waiting conflicts. Snapshot
protection assertions do not certify production key-ring deployment. Never replace these tests with method-order mirrors
of private implementation.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra.Orchestration
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Scheduler](../../../Application/Orchestration/Jobs/README.md)
- [Workflows](../../../Infrastructure/ApplicationServices/Orchestration/README.md)
- [Supervision](../../../Infrastructure/ApplicationServices/Orchestration/Local/README.md)
- [Durable runs](../../../Infrastructure/ApplicationServices/Orchestration/CloudRuns/README.md)

Warning regressions cover runtime quiescence while daemon collection survives, failed-stop resumption, stale identity
requests and startup reconciliation without historical resurrection. SQLite uses UTC ticks for chronological tests.
