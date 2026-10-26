# Deployment coordination

Deployment admission, local/cloud workflow ports, in-memory job scheduling and status notifications.

## Contents

| Module                     | Responsibility                                                                    |
|----------------------------|-----------------------------------------------------------------------------------|
| [Cloud](Cloud/README.md)   | Profile-specific launch admission, durable run port and cloud validation/options. |
| [Jobs](Jobs/README.md)     | Bounded in-memory queue, resource-aware scheduler and concurrency settings.       |
| [Local](Local/README.md)   | Local deployment and stop workflow contract.                                      |
| [Status](Status/README.md) | In-process deployment status events with isolated subscribers.                    |

## Usage and integration

Cloud assembles profile-specific launch requests; Jobs executes self-hosted queued jobs; Local defines Docker
deployment/stop workflows; Status broadcasts in-process state changes. Infrastructure provides deployment execution and
durable SaaS cloud scheduling. Follow leaf guides for implementation and test entry points.

## Contracts and lifecycle

The Jobs queue is memory-only; accepted jobs disappear on host exit. SaaS cloud runs use a separate PostgreSQL-backed
port and fenced workers. Independent local build/stop work is uncapped by default; cloud in-process concurrency defaults
to four. Same-project and conflicting-resource jobs remain ordered without consuming an execution lane while waiting.

Queued requests can contain credentials and mutable configuration. Do not log whole jobs or mutate configuration after
admission. Notifications are refresh hints, not durable state or proof of a successful deployment.

## Related documentation

- [Parent module](../README.md)
- [Execution implementations](../../Infrastructure/ApplicationServices/Orchestration/README.md)
- [Host capabilities](../Abstractions/Hosting/README.md)
- [Durable SaaS operations](../../docs/saas-cloud-deployments.md)
- [Scheduler regressions](../../Test/Infra/Orchestration/DeploymentJobWorkerTests.cs)
