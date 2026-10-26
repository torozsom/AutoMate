# Deployment request contracts

Configuration and launch inputs shared by local Docker and remote GitHub/Azure deployment workflows.

## Contents

| File                                                         | Types and responsibility                                                                                        |
|--------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------|
| [DeploymentConfigDto.cs](DeploymentConfigDto.cs)             | DeploymentConfigDto: selected application/C# project, runtime/cloud settings, databases and environment values. |
| [DatabaseConfigDto.cs](DatabaseConfigDto.cs)                 | DatabaseConfigDto: provider, credentials, connection-string key and container suffix.                           |
| [CloudDeploymentRequestDto.cs](CloudDeploymentRequestDto.cs) | CloudDeploymentRequestDto: repository launch inputs, credentials and optional durable-run lease context.        |

## Usage and integration

Prepare configuration for the selected application/C# project and attach database settings as
needed. [LocalDeploymentOrchestrator](../../../Infrastructure/ApplicationServices/Orchestration/Local/LocalDeploymentOrchestrator.cs)
consumes local settings. Cloud requests add repository and scanner metadata
for [CloudDeploymentOrchestrator](../../../Infrastructure/ApplicationServices/Orchestration/CloudPreparation/CloudDeploymentOrchestrator.cs); [CloudDeploymentRequestValidator](../../../Infrastructure/ApplicationServices/Orchestration/CloudPreparation/CloudDeploymentRequestValidator.cs)
checks launch inputs.

## Contracts and lifecycle

DeploymentConfigDto starts with local Development/8080 defaults; cloud preparation chooses cloud-appropriate values.
DatabaseConfigDto seeds template values from DeploymentDefaults. These models do not validate provider names, resource
availability or ownership.

CustomEnvVars, database passwords, GitHub tokens and Azure credentials are sensitive. Launch requests are unsuitable as
diagnostic messages or permanent history snapshots; use the separate non-secret deployment snapshot for history.

SaasRunId/SaasLeaseOwner carry durable-run identity and fencing context. DeferWorkflowMonitoring allows launch to return
after committing assets while separate workers monitor progress. These fields do not acquire or validate a lease
themselves.

## Related documentation

- [DTO index](../README.md)
- [Default constants](../../Defaults/README.md)
- [Deployment history](../../Entities/Deployments/README.md)
- [Durable cloud runs](../../Entities/Cloud/README.md)
- [Cloud configuration tests](../../../Test/App/Orchestration/CloudRuntimeConfigurationValidationTests.cs)
