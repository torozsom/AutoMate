# Deployment execution workflows

Local/cloud launch, durable SaaS scheduling, status persistence, snapshots and runtime supervision.

## Contents

| Module                                         | Responsibility                                                               |
|------------------------------------------------|------------------------------------------------------------------------------|
| [CloudPreparation](CloudPreparation/README.md) | Cloud validation, identity/artifact preparation and GitHub publication.      |
| [CloudRuns](CloudRuns/README.md)               | Durable SaaS admission, launch leases, webhook monitoring and retention.     |
| [Lifecycle](Lifecycle/README.md)               | Status persistence, snapshots, workflow progress and startup reconciliation. |
| [Local](Local/README.md)                       | Docker deployment/stop and host-owned diagnostic supervision.                |

## Usage and integration

CloudPreparation builds provider resources/GitHub artifacts. CloudRuns owns durable admission/leases/webhook
reconciliation. Local executes Docker and supervises sources. Lifecycle persists state/snapshots and reconciles startup.
Application owns ports and the separate in-memory scheduler.

## Contracts and lifecycle

Create deployment identity before reporting deployment-scoped failure; failures before persistence must not mutate an
unrelated latest deployment. Keep completion outcome independent of runtime state. Database capture drives automatic AI
wakeups, not project-only UI events.

Local source lifetimes are host-owned/awaited; SaaS releases launch slots before remote monitoring. Clock-aware
constructors retain system defaults. Exceptions can reach the separate bounded console sink; ordinary exports/customer
guidance remain safe.

## Related documentation

- [Parent module](../README.md)
- [Ports](../../../Application/Orchestration/README.md)
- [Failure capture](../../Data/README.md)
- [Diagnostics](../../Diagnostics/README.md)
- [Scheduling tests](../../../Test/Infra/Orchestration/DeploymentJobWorkerTests.cs)
