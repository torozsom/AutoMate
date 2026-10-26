# Durable SaaS cloud runs

Authorized admission, short fenced launches, shared cooldowns and independent workflow reconciliation.

## Contents

| File                                                         | Types or asset              | Responsibility                                                                        |
|--------------------------------------------------------------|-----------------------------|---------------------------------------------------------------------------------------|
| [CloudDeploymentRunService.cs](CloudDeploymentRunService.cs) | `CloudDeploymentRunService` | Authorizes and atomically admits a credential-free SaaS deployment.                   |
| [CloudDeploymentScheduler.cs](CloudDeploymentScheduler.cs)   | `CloudDeploymentScheduler`  | Runs short SaaS launch tasks and renews their database leases across host instances.  |
| [CloudRunMetricsService.cs](CloudRunMetricsService.cs)       | `CloudRunMetricsService`    | Samples cluster-wide queue and monitor state for autoscaling and alerts.              |
| [CloudRunMonitor.cs](CloudRunMonitor.cs)                     | `CloudRunMonitor`           | Applies verified workflow events and reconciles runs without occupying launch slots.  |
| [CloudRunMonitorService.cs](CloudRunMonitorService.cs)       | `CloudRunMonitorService`    | Consumes webhook receipts and reconciles missing GitHub events across SaaS instances. |
| [CloudRunProcessor.cs](CloudRunProcessor.cs)                 | `CloudRunProcessor`         | Claims SaaS launches with cluster-wide admission and executes a short launch phase.   |
| [CloudRunRetentionService.cs](CloudRunRetentionService.cs)   | `CloudRunRetentionService`  | Removes transient webhook and outbox metadata after the diagnostic retention window.  |
| [CloudRunSnapshot.cs](CloudRunSnapshot.cs)                   | `CloudRunSnapshot`          | Protected, token-free inputs needed to resume a SaaS launch on another worker.        |

## Usage and integration

CloudDeploymentRunService atomically admits idempotent runs/outbox wakeups and protected snapshots. Scheduler
claims/renews launch work through CloudRunProcessor. CloudRunMonitor/MonitorService apply verified deliveries and
reconcile missing events. MetricsService samples shared state; RetentionService removes transient receipts/outbox
metadata.

## Contracts and lifecycle

Owner/project/installation/global admission limits use short transactions, not locks around providers. Workers acquire
fresh installation/ARM tokens; tokens are not queued. CloudRunSnapshot can contain secret environment values and uses
the dedicated protected converter. Leases fence ownership; throttling pauses the installation.

Remote workflow monitoring does not occupy a launch slot. Match installation/repository/commit/branch/workflow identity
before state changes; deliveries are deduplicated and recoverably leased. Preserve safe guidance and independent
deployment outcome. Transient metadata retention is not permanent history deletion. Explicit clocks retain existing
sampling/constructor behavior.

## Related documentation

- [Parent module](../README.md)
- [Run port/options](../../../../Application/Orchestration/Cloud/README.md)
- [Models](../../../../Domain/Entities/Cloud/README.md)
- [Ingress](../../../GitHub/Webhooks/README.md)
- [Tests](../../../../Test/Infra/Orchestration/CloudDeploymentRunServiceTests.cs)
- [Operations](../../../../docs/saas-cloud-deployments.md)
