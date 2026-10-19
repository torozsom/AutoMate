# Cloud launch preparation

Validation, project resolution, deterministic naming, identity/resources and GitHub artifact publication.

## Contents

| File                                                                     | Types or asset                    | Responsibility                                                                                                |
|--------------------------------------------------------------------------|-----------------------------------|---------------------------------------------------------------------------------------------------------------|
| [CloudCsProjectResolver.cs](CloudCsProjectResolver.cs)                   | `CloudCsProjectResolver`          | Resolves or creates the C# project record used by cloud deployment records.                                   |
| [CloudDeploymentDefaults.cs](CloudDeploymentDefaults.cs)                 | `CloudDeploymentDefaults`         | Applies default Azure resource names for cloud deployment configuration.                                      |
| [CloudDeploymentOrchestrator.cs](CloudDeploymentOrchestrator.cs)         | `CloudDeploymentOrchestrator`     | Orchestrates cloud deployment preparation by generating IaC and workflow files and committing them to GitHub. |
| [CloudDeploymentRequestValidator.cs](CloudDeploymentRequestValidator.cs) | `CloudDeploymentRequestValidator` | Validates required inputs for cloud deployment orchestration.                                                 |
| [CloudRepositorySecretBuilder.cs](CloudRepositorySecretBuilder.cs)       | `CloudRepositorySecretBuilder`    | Builds GitHub Actions repository secrets required by generated cloud deployment workflows.                    |
| [OrchestrationNameNormalizer.cs](OrchestrationNameNormalizer.cs)         | `OrchestrationNameNormalizer`     | Normalizes deployment names used by Docker Compose, containers, image tags, and cloud resources.              |

## Usage and integration

CloudDeploymentOrchestrator uses request validation, project/default resolution and secret/model generation. It
refreshes ARM credentials, prepares Azure providers/identities/registry, captures history and publishes
artifacts/secrets. It either observes the workflow or returns for durable monitoring.

## Contracts and lifecycle

Requests contain credentials/mutable configuration: do not log them or mutate after admission. Capture non-secret
history before side effects and enrich only verified commit/image/runtime facts. Stale SaaS lease owners must not
publish.

Secret naming and workflow destinations must agree with Templates/Models and Domain defaults. Errors use fixed customer
guidance/failed deployment history rather than provider prose. Status remains Starting until verified workflow success.
Rendering alone proves neither repository permission nor provisioning success.

## Related documentation

- [Parent module](../README.md)
- [Admission](../../../../Application/Orchestration/Cloud/README.md)
- [Setup](../../../Azure/Provisioning/README.md)
- [Artifacts](../../../Templating/README.md)
- [Publication](../../../GitHub/README.md)
- [Privacy tests](../../../../Test/Infra/Orchestration/CloudFailurePrivacyTests.cs)
- [Recovery tests](../../../../Test/Infra/GitHub/GitHubSaasCommitRecoveryTests.cs)
