# Azure adapter ports

Federated-identity provisioning, Container Apps runtime collection and delegated Azure Monitor token acquisition.

## Contents

| File                                                                         | Types                                                           | Responsibility                                                                                     |
|------------------------------------------------------------------------------|-----------------------------------------------------------------|----------------------------------------------------------------------------------------------------|
| [IAzureContainerAppRuntimeStreamer.cs](IAzureContainerAppRuntimeStreamer.cs) | `IAzureContainerAppRuntimeStreamer`                             | Supervises host-managed Azure runtime log and metric collection for verified deployment resources. |
| [IAzureDeploymentOrchestrator.cs](IAzureDeploymentOrchestrator.cs)           | `IAzureDeploymentOrchestrator`                                  | Configures Azure resources required for GitHub Actions OIDC cloud deployments.                     |
| [IAzureMonitorLogsTokenProvider.cs](IAzureMonitorLogsTokenProvider.cs)       | `IAzureMonitorLogsTokenProvider`, `AzureMonitorLogsTokenResult` | Obtains a short-lived Azure Monitor Logs data-plane token for a connected AutoMate user.           |

## Usage and integration

Cloud preparation calls IAzureDeploymentOrchestrator for GitHub/Azure trust setup. Successful deployments are monitored
through IAzureContainerAppRuntimeStreamer. Monitor collectors obtain current Logs-scoped tokens through
IAzureMonitorLogsTokenProvider rather than handling refresh tokens themselves.

## Contracts and lifecycle

AzureMonitorLogsTokenResult.IsSuccess depends on a nonblank token; FailureReason provides safe unavailability guidance.
ARM credentials and Monitor Logs credentials have different scopes. StartStreaming starts supervised work and returns
void, so it is not a completion task or durable admission receipt. Request DTOs contain tokens and configuration; never
log them. Provider implementations own authorization, refresh, cancellation and SDK details.

## Related documentation

- [Parent module](../README.md)
- [Federation implementation](../../../Infrastructure/Azure/Provisioning/AzureDeploymentOrchestrator.cs)
- [Runtime streamer](../../../Infrastructure/Azure/ContainerApps/AzureContainerAppRuntimeStreamer.cs)
- [Monitor token implementation](../../../Infrastructure/Azure/Monitor/AzureMonitorLogsTokenProvider.cs)
- [Azure DTOs](../../../Domain/DTO/Azure/README.md)
- [Token tests](../../../Test/Infra/Azure/Monitor/AzureMonitorLogsTokenProviderTests.cs)
