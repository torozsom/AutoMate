# Azure

Azure Container Apps and OIDC infrastructure adapters.
For SaaS, launch workers refresh ARM credentials on demand, ensure a customer ACR exists, assign separate push and pull
identities, and use the pull identity for Container Apps image access. ACR creation and role assignment require the
connected customer's Azure account to have sufficient permission.

Container Apps availability, metrics, console output, and system/revision events are normalized into deployment
diagnostics before terminal delivery. `AzureContainerAppRuntimeStreamer` is a host-managed coordinator; it queries
the Azure Monitor resource-scoped Logs API with a memory-only token and persists timestamp/hash cursors, never log
content or OAuth tokens.

## Source inventory

- `AzureConstants.cs`
- `AzureContainerAppClient.cs`
- `AzureContainerAppMetrics.cs`
- `AzureContainerAppRuntimeStreamer.cs`
- `AzureContainerAppLogCheckpointStore.cs`
- `AzureMonitorLogsClient.cs`
- `AzureMonitorLogsOptions.cs`
- `AzureMonitorLogsTokenProvider.cs`
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

Infrastructure may reference Application and Domain, but never Web. It implements ports and owns provider-specific
behavior.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)
