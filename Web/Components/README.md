# Components

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

ProjectTelemetrySummary receives the already resolved owner from ProjectDetails and creates a separate dependency scope for each analytics load. This prevents its EF queries from sharing the parent's circuit context during overlapping rendering/history loads; the application service still checks ownership.
