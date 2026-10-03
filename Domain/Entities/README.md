# Entities

Persisted business entities with no EF Core or transport dependencies.

## Source inventory

- `Application.cs`
- `AzureContainerAppLogCheckpoint.cs`
- `BaseEntity.cs`
- `Configuration.cs`
- `CsProject.cs`
- `CloudDeploymentRun.cs`, `CloudRunOutbox.cs`, `CloudWebhookDelivery.cs`, `CloudInstallationBudget.cs` — SaaS
  control-plane state, verified webhook inbox, and installation cooldowns.
- `CloudDeploymentRun.cs`, `CloudRunOutbox.cs`, `CloudWebhookDelivery.cs`, `CloudInstallationBudget.cs` — SaaS
  control-plane state, verified webhook inbox, and installation cooldowns.
- `Deployment.cs`
- `GitHubWorkflowCheckpoint.cs`
- `GitHubWorkflowJobCheckpoint.cs`
- `LocalUser.cs`
- `RemoteUser.cs`
- `User.cs`

## Boundary

Keep this module independent of Application, Infrastructure, Web, framework APIs, and provider SDKs.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)

`TelemetryTenantState.cs` owns provider-neutral leases, rate windows and bounded loss/series state. Application runtime
and managed-egress preferences are explicit default-off consent fields.
