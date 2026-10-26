# Azure workflow contracts

Azure account, federated-identity setup and Container Apps monitoring inputs without Azure SDK dependencies.

## Contents

| File                                                                                 | Types and responsibility                                                                                 |
|--------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------|
| [AzureCloudCredentialsDto.cs](AzureCloudCredentialsDto.cs)                           | AzureCloudCredentialsDto: tenant/subscription identity and delegated ARM access token.                   |
| [AzureContainerAppRuntimeStreamRequest.cs](AzureContainerAppRuntimeStreamRequest.cs) | AzureContainerAppRuntimeStreamRequest: project, deployment, user, revision and configuration to monitor. |
| [AzureOidcSetupResultDto.cs](AzureOidcSetupResultDto.cs)                             | AzureOidcSetupResultDto: managed identities and GitHub Actions federated-trust identifiers.              |

## Usage and integration

[AzureArmCredentialsProvider](../../../Infrastructure/Azure/Identity/AzureArmCredentialsProvider.cs) resolves
credentials. [IAzureDeploymentOrchestrator](../../../Application/Abstractions/Azure/IAzureDeploymentOrchestrator.cs)
returns OIDC setup details. Pass deployment-specific requests
through [IAzureContainerAppRuntimeStreamer](../../../Application/Abstractions/Azure/IAzureContainerAppRuntimeStreamer.cs); [AzureContainerAppRuntimeStreamer](../../../Infrastructure/Azure/ContainerApps/AzureContainerAppRuntimeStreamer.cs)
implements monitoring.

## Contracts and lifecycle

AzureCloudCredentialsDto contains an access token and must not be logged. A stream request also contains credentials and
deployment configuration; UserId identifies the account for resolving current monitoring authorization. ExpectedRevision
can be null when no revision was recorded.

OIDC setup returns trust and managed-identity identifiers, including an optional dedicated registry-pull identity. The
DTO does not provision those resources, refresh tokens, encrypt credentials or validate authorization.

## Related documentation

- [DTO index](../README.md)
- [Deployment inputs](../Deployment/README.md)
- [Azure adapters](../../../Infrastructure/Azure/README.md)
- [Azure regressions](../../../Test/Infra/Azure/README.md)
