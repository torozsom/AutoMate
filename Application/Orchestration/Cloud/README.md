# Cloud deployment admission

Profile-specific request assembly, cloud launch/run contracts, form validation and cloud execution settings.

## Contents

| File                                                                             | Types                                                                          | Responsibility                                                                                                        |
|----------------------------------------------------------------------------------|--------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------|
| [CloudDeploymentAdmission.cs](CloudDeploymentAdmission.cs)                       | `CloudDeploymentAdmissionRequest`, `CloudDeploymentAdmission`                  | Carries verified connection inputs for one cloud admission; credentials must never enter diagnostics.                 |
| [CloudDeploymentStart.cs](CloudDeploymentStart.cs)                               | `CloudDeploymentStart`, `CloudDeploymentReceipt`, `ICloudDeploymentRunService` | Inputs for an authorized, durable SaaS cloud deployment request.                                                      |
| [CloudRuntimeConfigurationValidation.cs](CloudRuntimeConfigurationValidation.cs) | `CloudRuntimeConfigurationValidation`                                          | Preserves cloud form validation rules and first-error precedence independently of rendering and provider constraints. |
| [CloudSaasOptions.cs](CloudSaasOptions.cs)                                       | `CloudSaasOptions`                                                             | Cluster-wide SaaS cloud admission limits.                                                                             |
| [GitHubWorkflowMonitoringOptions.cs](GitHubWorkflowMonitoringOptions.cs)         | `GitHubWorkflowMonitoringOptions`                                              | Bounds GitHub Actions monitoring and untrusted log delivery work.                                                     |
| [ICloudDeploymentOrchestrator.cs](ICloudDeploymentOrchestrator.cs)               | `ICloudDeploymentOrchestrator`                                                 | Service responsible for coordinating cloud deployment asset generation and GitHub commit workflow.                    |

## Usage and integration

CloudDeploymentAdmission.StartAsync assembles a fixed net10.0/10.0 web-project metadata projection from a confirmed
request. With local capability enabled it enqueues CloudDeploymentJob and returns null; without local capability it
calls ICloudDeploymentRunService and returns a durable receipt. Web's connected-cloud admission prepares account
credentials and project scope before this step.

ICloudDeploymentOrchestrator executes launch preparation. ICloudDeploymentRunService admits/reads owner-scoped durable
SaaS runs; Infrastructure separates launch leases from workflow monitoring. CloudRuntimeConfigurationValidation owns
existing form checks and first-error ordering, not all provider/server validation.

## Contracts and lifecycle

CloudDeploymentAdmissionRequest and CloudDeploymentStart are memory-only and secret-bearing. Never log full requests.
The mutable Config reference is retained for compatibility: callers must stop mutating it after admission. The SaaS
branch requires its caller-provided idempotency key; page-owned keys are cleared after successful admission. A receipt
describes phase/queue time and safe failure guidance, not deployment health.

CloudSaas options default to ten queued/user, two active/user, four active/installation, 32 globally, eight/worker and
two-second polling. These durable limits differ from in-memory DeploymentConcurrency settings. GitHubWorkflowMonitoring
defaults to ten-second polling and sixty-minute maximum monitoring.

Runtime validation checks supported database engines, connection-key uniqueness, required names/login/password rules and
environment-name collisions in existing order. It returns the first error or null. It does not authorize
repository/resource access or replace provider validation.

## Related documentation

- [Parent module](../README.md)
- [Launch implementation](../../../Infrastructure/ApplicationServices/Orchestration/CloudPreparation/CloudDeploymentOrchestrator.cs)
- [Durable admission](../../../Infrastructure/ApplicationServices/Orchestration/CloudRuns/CloudDeploymentRunService.cs)
- [Connected-account consumer](../../../Web/Services/ConnectedCloudDeploymentAdmission.cs)
- [Self-hosted queue](../Jobs/README.md)
- [Routing tests](../../../Test/App/Orchestration/CloudDeploymentAdmissionTests.cs)
- [Form validation tests](../../../Test/App/Orchestration/CloudRuntimeConfigurationValidationTests.cs)
- [SaaS operations](../../../docs/saas-cloud-deployments.md)
