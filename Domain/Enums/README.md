# Enums

Domain classifications for sources, application types, and deployment state.

## Source inventory

- `AppType.cs`
- `DeploymentStatus.cs`
- `GitHubWorkflowLogAvailability.cs`
- `SourceType.cs`

## Boundary

Keep this module independent of Application, Infrastructure, Web, framework APIs, and provider SDKs.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)

AiAnalysisStatus retains existing persisted values 0–4 and appends Cancelled = 5. It is a terminal owner-requested
state, excluded from claims and renewals; canceled results can be deleted or expire under normal retention.
