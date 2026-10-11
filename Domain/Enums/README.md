# Enums

Domain classifications for sources, application types, and deployment state.

## Source inventory

- `Projects/AppType.cs`
- `Deployments/DeploymentStatus.cs`
- `GitHub/GitHubWorkflowLogAvailability.cs`
- `Projects/SourceType.cs`

## Boundary

Keep this module independent of Application, Infrastructure, Web, framework APIs, and provider SDKs.

## Related documentation

- [Solution navigation map](../../navigation.md)

AiAnalysisStatus retains existing persisted values 0–4 and appends Cancelled = 5. It is a terminal owner-requested
state, excluded from claims and renewals; canceled results can be deleted or expire under normal retention.

## Folder organization

- `Ai/` — analysis states and triggers.
- `Deployments/` — runtime status, preparation outcome and cloud run phases.
- `Projects/` — application and source classifications.
- `GitHub/` — workflow log availability.

The Domain.Enums namespace and persisted enum values are unchanged.
