# Web

Blazor Server presentation, HTTP endpoints, SignalR, and the composition root.

For local IDE development, run `./deploy/manage.ps1 start -Ide` from the repository root (or
`bash deploy/manage.sh start --ide`), then select Web's `https` launch profile in the IDE. Web listens at
`https://localhost:7288`; Docker hosts PostgreSQL, Redis and Telemetry. The Docker Web container stays stopped.
See [IDE configuration and OAuth callbacks](../deploy/README.md#preferred-workflow-web-in-the-ide).

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

The Projects list offers Deploy, Stop and Remove in that order. Stop queues the same owner-authorized local Docker
operation as Project Details; it is unavailable for inactive projects, disabled local hosting and cloud projects (which
use the provider portal). Pending stops disable competing row actions. Project Details terminals use a taller
440–640px viewport and 94% desktop width, retaining full width on mobile.

The Obsidian Telemetry refresh follows the three reference subfolders under `.agents/UI-Design` through native Blazor
components and `wwwroot/obsidian.css`. Dark is the default for new sessions; saved light/dark choices load before
the first paint. Inter and JetBrains Mono plus the existing Bootstrap Icons are served locally with their licenses.
Reference-only controls and illustrative claims are omitted; current application features are preserved.

## Metric refinements

Every chart group uses a shared UTC range picker (10-minute through five-year presets, custom five-minute minimum).
Numeric metric/outcome tables use independent 10/25/50-row pagination. Completed AI answers have a bright purple border;
context settings, consent, request status and run metadata stay outside that border. Repository
source pages retain their earlier card presentation. See ../docs/metric-exploration.md.

## Source organization

See the [solution folder map](../navigation.md#responsibility-folders) for module subdivisions. Folder moves preserve
existing .NET namespaces and public contracts. Generated build outputs and third-party assets retain their standard
layouts.

## Source documentation

Handwritten C# types and callables, including private helpers and inline Razor code, use multiline XML summaries,
matching parameter/type-parameter tags, and return descriptions for value-returning and asynchronous operations.
Document supported escaping exceptions; handled failures belong in the behavior description. Fields and properties
may use single-line XML summaries. Component-level descriptions use Razor `@* ... *@` comments so they do not render
into HTML. Browser functions and nested callbacks use multiline JSDoc with typed parameters, returns and applicable
failure contracts. Preserve generated code and vendored Bootstrap distributions and licenses.

The complete local coverage and behavior-preservation checks are recorded in the ignored
`.artifacts/web-doc-audit/report.md` verification artifact.
