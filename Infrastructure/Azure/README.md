# Azure

Azure Container Apps and OIDC infrastructure adapters.

Container Apps availability state and metrics are normalized into deployment diagnostics before terminal delivery.
Console and system Log Analytics tailing remain a subsequent ingestion milestone.

## Source inventory

- `AzureConstants.cs`
- `AzureContainerAppClient.cs`
- `AzureContainerAppMetrics.cs`
- `AzureContainerAppRuntimeStreamer.cs`
- `AzureContainerAppState.cs`
- `AzureDeploymentOrchestrator.cs`
- `AzureFederatedCredentialService.cs`
- `AzureManagedIdentityProvisioner.cs`
- `AzureOidcSetupContext.cs`
- `AzureOidcSetupPlanner.cs`
- `AzureResourceProviderRegistrar.cs`
- `AzureRoleAssignmentService.cs`
- `StaticAccessTokenCredential.cs`

## Boundary

Infrastructure may reference Application and Domain, but never Web. It implements ports and owns provider-specific behavior.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)
