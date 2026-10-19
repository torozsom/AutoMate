# Durable cloud execution

SaaS control-plane metadata for admitted launches, transactional wakeups, verified webhooks and installation throttling.

## Contents

| File                                                     | Types and responsibility                                                                                |
|----------------------------------------------------------|---------------------------------------------------------------------------------------------------------|
| [CloudDeploymentRun.cs](CloudDeploymentRun.cs)           | CloudDeploymentRun: launch snapshot, repository/workflow correlation, execution phase and worker lease. |
| [CloudRunOutbox.cs](CloudRunOutbox.cs)                   | CloudRunOutbox: transactional admission wakeup and dispatch timestamp.                                  |
| [CloudWebhookDelivery.cs](CloudWebhookDelivery.cs)       | CloudWebhookDelivery: verified workflow receipt, delivery identity and recoverable processing lease.    |
| [CloudInstallationBudget.cs](CloudInstallationBudget.cs) | CloudInstallationBudget: installation-wide provider-throttle cooldown and observation count.            |

## Usage and integration

Start
with [CloudDeploymentRunService](../../../Infrastructure/ApplicationServices/Orchestration/CloudRuns/CloudDeploymentRunService.cs)
and [CloudDeploymentScheduler](../../../Infrastructure/ApplicationServices/Orchestration/CloudRuns/CloudDeploymentScheduler.cs).
Launch processors consume snapshots; monitoring reconciles GitHub
workflows. [GitHubWebhookReceiver](../../../Infrastructure/GitHub/Webhooks/GitHubWebhookReceiver.cs) verifies ingress
before durable receipts are processed.

## Contracts and lifecycle

CloudDeploymentRun begins Queued and records lease/retry timing separately from workflow progress. SnapshotJson holds
token-free launch inputs but can include secret-bearing custom environment values. Infrastructure applies a dedicated
protected-string conversion; the entity itself does not encrypt values.

CloudRunOutbox records wakeup dispatch. CloudWebhookDelivery retains correlation/deduplication metadata and a
recoverable lease, not the raw webhook body. CloudInstallationBudget is keyed by InstallationId and shares throttle
cooldowns across workers.

These records support launch orchestration; they do not replace Deployment history or runtime status. CloudRunPhase is
stored as a string by the current EF mapping, so its names are compatibility-sensitive.

## Related documentation

- [Entities index](../README.md)
- [Mappings and protection](../../../Infrastructure/Data/AutoMateModelConfiguration.cs)
- [Run phases and deployment states](../../Enums/Deployments/README.md)
- [Cloud run tests](../../../Test/Infra/Orchestration/CloudDeploymentRunServiceTests.cs)
- [SaaS operations](../../../docs/saas-cloud-deployments.md)
