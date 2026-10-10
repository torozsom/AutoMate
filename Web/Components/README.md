# Components

Dashboard deployment-status notifications marshal model changes and rendering together through InvokeAsync.
Async callback failures are observed and logged with fixed guidance, GUIDs and failure types; queued callbacks after
disposal are ignored. DashboardStatusTests exercises Running/Failed/Stopped notifications from background tasks using
a real renderer dispatcher, including disposal.

## Telemetry presentation

Project details separates live utilization and Project Analytics. Deployment Details opens with an overview, saved logs
and historical resource summaries. Charts and numeric tables are collapsed by default;
range changes preserve log paging and channels. Independent asynchronous scopes isolate history and analytics database
reads, and cancelled/stale responses cannot overwrite newer selections.

`Shared/TelemetryChart.razor` renders labeled SVG charts, visible isolated observations and gaps. Its local
`wwwroot/js/telemetry-chart.js` module handles pointer/touch and arrow-key inspection without server roundtrips,
maintains legible axis text when resized, and disposes listeners/observers. Shared presentation styles use AutoMate's
light/dark theme tokens. `Shared/TelemetryPresentation.cs` owns friendly names, binary memory units, sub-core precision,
rounded axes and sample-weighted daily averages. Historical summaries average returned interval aggregates; raw sample
counts are unavailable there. Memory capacity remains supporting text rather than flattening the usage chart.

Collection consent remains explicit; retention/sampling help is collapsed. No storage/API/schema changes are involved.
Rendering fixtures in Web.Tests generate ignored `.artifacts/metrics-preview` HTML for browser checks without OAuth.

Project details offers Stop only for local sources. Remote cloud deployments must be stopped in the provider portal;
the handler also rejects remote sources so their relative project paths never enter the local filesystem scanner.

Blazor components and their presentation-specific code-behind files.
The project details page restores bounded terminal history for the latest deployment after joining its authorized
SignalR group, merges buffered live events by database cursor, and repeats that handshake after reconnecting.
It starts this handshake only after project loading has completed and terminal components have rendered; Blazor can
render the loading view before `OnInitializedAsync` finishes.
The shared `Terminal` component also queues writes until xterm finishes JavaScript initialization, so a fast replay
cannot disappear during the first render. Its pre-init queue has a fixed character limit and reports overflow.
SignalR startup and replay use the page's cancellation token; navigation cancels them without a connection-failure
notice. Genuine connection and storage failures remain observable.
Dashboard and project details read the process-local queue state, showing “Queued...” until a deployment or stop job
starts. State changes notify both views without exposing the queued request's credentials.
In SaaS mode, cloud submissions use the durable Application admission service. Project details polls the authorized
run phase and incremental redacted terminal history so status and logs catch up across AutoMate instances.
In SaaS mode, cloud submissions use the durable Application admission service. Project details polls the authorized
run phase and incremental redacted terminal history so status and logs catch up across AutoMate instances.

## Source inventory

- `_Imports.razor`
- `App.razor`
- `Routes.razor`

## Boundary

Keep presentation behavior here. Components, hubs, routes, and services use Application contracts; concrete
Infrastructure types are registered only in Web/Configs.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)

`Pages/DeploymentHistory.razor` provides historical channel pages and CPU/memory charts.
Its isolated stylesheet bounds the terminal viewport so xterm fitting cannot grow the page. Saved output is rendered
independently of metric queries; metric-provider failures keep the logs visible and show a separate availability notice.
Changing history pages or channels replaces terminal content instead of accumulating it. The shared xterm wrapper
coalesces resize notifications and fits only when the viewport dimensions change.
`Shared/TelemetryPreferences.razor`
records explicit runtime and managed-provider consent. Project replay tracks a confirmed cursor separately from bounded
rendered live identities and does not advance on failed backend reads. Self-hosted pages also periodically catch up.

Runtime logs and metrics collected while viewing are saved for 30-day replay. The checkbox enables background
collection while the page is closed. Metric history defaults to 60-second sampling; local live cards update with each
Docker stats observation (normally every 1–2 seconds). The metric cards restore the latest
saved numeric snapshot while awaiting live updates. `DeploymentMetricDisplay` formats numeric units for the cards.

## Deployment telemetry update

Both SelfHosted and SaaS use the private Telemetry disk gateway for new deployment logs and metrics. PostgreSQL payload
writes are rejected at application startup; legacy reads and draining of existing outbox rows remain available. The
gateway confirms durable checksummed writes before cloud checkpoints advance. Tenant-scoped v2 history, deployment
revision recovery and weighted daily project analytics are documented in [the rollout guide](/docs/saas-telemetry.md).
Detailed data expires after 30 days; daily statistics after 365 days. See the root navigation.md for new module entry
points.

ProjectTelemetrySummary receives the already resolved owner from ProjectDetails and creates a separate dependency scope
for each analytics load. This prevents its EF queries from sharing the parent's circuit context during overlapping
rendering/history loads; the application service still checks ownership.

`Shared/DeploymentTerminalPresentation.cs` formats live and saved stderr with a plain `[stderr]` marker, preserving
line endings and legacy/stdout text. Stored event messages remain unchanged so replay identities remain stable.
The existing Build tab also receives strictly owned local Docker lifecycle and subscription notices.

Deployment preparation/queue failures show fixed guidance rather than provider exception text. The known local
queue-full guidance is retained. Project status/SignalR operational failures log GUIDs and failure types without
exception objects; terminal replay and reconnection behavior are unchanged.

DeploymentAnalysisPanel is a passive owner-result presenter on ProjectDetails. It renders durable state/timestamps,
trigger and completed provenance/summary/steps/evidence through normal Razor encoding; partial nonterminal fields and
results from another deployment are omitted. Analyze and Refresh use native buttons, live textual status, existing
theme tokens and wrapping touch targets. Actual deployment consent and operator enablement control presentation;
server ports continue to enforce authorization and egress policy. ProjectDetails retains a stable request GUID only
when admission is uncertain, serializes owner actions and displays fixed errors without exception payloads. Explicit
consent editing targets only the current deployment's configured C# project through the exact-project Application
overload. Remote projects without configuration can edit consent; the service creates their missing configuration,
and the page reads it back before enabling analysis. Cancel uses the existing owner-authorized idempotent cancellation
and saved readback; it remains available
for queued/running work when AI or consent is disabled. Consent changes do not enqueue work or change operator policy.
Linked consent guidance explains data egress and revocation limits. Both handlers fence stale feedback and use fresh
scopes; uncertain cancellation can safely be retried for the same analysis. Native controls include visible focus
outlines and sufficiently contrasting borders in both themes.

Analysis request/read actions resolve their Application port through fresh asynchronous scopes, avoiding concurrent
DbContext use with page/background reads. The latest deployment is rechecked after awaits before publishing a view.
An independent five-second analysis timer runs in both hosting profiles on the renderer dispatcher. Versioned reads
capture owner/deployment identity, preserve the saved view on failure, clear temporary polling warnings after recovery
and ignore late results/feedback. Busy owner actions skip ticks; page disposal cancels and awaits polling.

## Deployment history update (2026-10-08)

ProjectSectionNavigation uses a scoped disposable JS module for fragments, focus, history, offsets and reduced motion.
DeploymentAnalysisSection shares fenced consent/actions and five-second polling across both pages, with paged saved
runs. DeploymentConfigurationDetails shows only recorded snapshots/artifacts; unavailable legacy configuration is
explicit. Deployment Details presents selected-deployment logs before historical metrics and AI.

Project Details ignores saved metric recovery callbacks and late provider replies after page disposal. Lifetime
cancellation remains available until hub disposal completes; expected navigation cancellation/disposal does not emit a
recovery failure. Real provider failures retain redacted exception diagnostics.

## Status-aware assessment presenter (2026-10-09)

`AssessmentContextControls` is shared through `DeploymentAnalysisSection` on both deployment surfaces. It edits focus,
source/channel/metric selection and UTC range; per-deployment preferences are separate from immutable pending requests.
Submitted controls freeze while pending. Route/disposal fencing and five-second result polling remain; context catalog
refresh is bounded to once per minute. `AssessmentResultSection` and `DeploymentAnalysisPanel` render validated v2
sections and saved provenance while retaining legacy layouts. See
[status-aware assessments](../../docs/status-aware-assessments.md).

## Workspace console

WorkspaceOverviewPanel reads IWorkspaceQuery in independent scopes, refreshes visible pages every thirty seconds and
fences replies against identity changes/disposal. WorkspaceOverviewView, KpiCard and ProjectInventoryTable are passive
read-model presenters. Dashboard URL parameters preserve inventory selection; an owned entity is loaded only for the
selected deployment action. ConsoleDialog and js/console-ui.js own keyboard focus, Escape and restoration; native auth
forms keep antiforgery. RecordedValueRow copies explicit non-secret deployment metadata. See docs/ui-modernization.md.

## Shared range and numeric controls

MetricRangePicker freezes UTC bounds, validates future/calendar-year limits, and explicitly clips long deployment
lifetimes. MetricStatisticsView renders bounded sample-weighted charts and paginated per-container/deployment
statistics; StatisticsPager changes only numeric rows. Overview relative selections advance on visible refresh; custom
ranges remain fixed. Project/history requests cancel obsolete reads and discard stale responses. These chart controls
do not edit AI context preferences or saved results.
