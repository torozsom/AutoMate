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
