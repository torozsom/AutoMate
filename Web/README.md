# Web

Blazor Server presentation, HTTP endpoints, SignalR, and the composition root.

## Source inventory

- `appsettings.Development.json`
- `appsettings.json`
- `Program.cs`
- `Web.csproj`
- `Web.csproj.user`

## Boundary

Keep presentation behavior here. Components, hubs, routes, and services use Application contracts; concrete
Infrastructure types are registered only in Web/Configs.

## Related documentation

- [Application](../Application/README.md)
- [Infrastructure](../Infrastructure/README.md)

## Deployment analysis

ProjectDetails uses Application ports for owner-authorized analysis actions and five-second persisted-state polling.
DeploymentAnalysisPanel renders consent, request/refresh/cancel controls, finite status guidance and validated text/
provenance with accessible live status. Deletion is exposed through authenticated antiforgery-protected HTTP routes;
the panel has no delete control. Concrete provider/EF services, workers and safe OpenTelemetry export are composed only
in Configs. Default-off feature/automatic/egress flags and zero USD spending preserve disabled startup without a key.
See [architecture/onboarding](../docs/ai-analysis.md), [operations](../docs/ai-analysis-operations.md) and
[internal-pilot acceptance](../docs/ai-analysis-rollout.md). Live provider approval and rollout remain pending.

Platform logging/trace export policy lives in [Observability](Observability/README.md), registered in Web/Configs.

`/health` provides lightweight liveness. `/health/ready` provides bounded, read-only AI configuration/queue readiness
without LLM requests. See [probe status and operational limits](Configs/README.md).

Azure OpenAI API-key analysis is registered in both hosting profiles. Follow
[Azure Portal and Web user-secrets setup](../docs/azure-openai-setup.md); default-off flags, project consent and
positive
reviewed spend limits still govern provider execution. Web already has the required local UserSecretsId.

## Deployment history update (2026-10-08)

Project Details anchors follow Overview, Live Logs, Live Resource Utilization, Project Analytics, AI Analysis,
Configuration and Deployment History. Each six-item history page opens View Details on the existing deployment route.
Recorded configuration is separate from next-deployment settings.

## Console modernization

Signed-in Home is Overview and /dashboard is Projects. Public pages use a lightweight header; shared local theme tokens
and console.css provide compact tables, KPI cards and responsive surfaces.
See [behavior and verification](../docs/ui-modernization.md).

## Metric refinements

Every chart group uses a shared UTC range picker (10-minute through five-year presets, custom five-minute minimum).
Numeric metric/outcome tables use independent 10/25/50-row pagination. AI sections have a purple border; repository
source pages retain their earlier card presentation. See ../docs/metric-exploration.md.
