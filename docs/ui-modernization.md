# SaaS console UI

## Design and page roles

The local TypeUI fundamentals skill guides hierarchy, proximity, keyboard interaction and accessibility.
No TypeUI MCP server was exposed during this implementation. Reference research used the
[Linear design refresh](https://linear.app/now/behind-the-latest-design-refresh) and
[Carbon data-table guidance](https://www.carbondesignsystem.com/building-blocks/core/components/data-table/guidelines).

Existing theme tokens now use neutral surfaces with restrained blue actions. The console uses system fonts,
4px spacing increments, 8px corners, readable metadata and semantic tables. There is no new framework/font dependency.

- Signed-in Home (`/`) is **Overview**. Projects (`/dashboard`) is the paginated inventory.
- Overview defaults to the last 30 days. Chart range and numeric pagination refinements are documented
  in [Metric exploration](metric-exploration.md).
- Deployment counts include all owned component records, not recent-history previews.
- Success rate is succeeded / (succeeded + failed). Unknown outcomes are displayed separately.
- Runtime status and recorded completion outcome are distinct. A stopped successful deployment stays successful.
- Completed duration is FinishedAt minus CreatedAt; invalid/missing durations are omitted.
- Daily CPU/memory means are weighted by sample count. These are observed per-container averages, not fleet totals,
  capacity percentages or health judgments. Extrema span observed containers; absent days remain gaps.
- Resource availability is isolated from deployment statistics. Existing aggregates can arrive later than live data.
- Refresh runs every 30 seconds while visible, plus manual refresh and deployment notifications.
- Projects uses twenty rows per page. Search/source/status/sort/page are encoded in the URL. Latest deployment selection
  spans all C# components and has a stable identity tie-breaker.
- Cloud requests without a deployment are shown separately under attention. Only the latest failed/starting component
  record is treated as current attention.
- Recorded configuration uses grouped tables and expandable provider identities. Missing legacy snapshots remain
  unknown.
  Next-deployment editing remains separate. Secret values are not part of the read models.
- Public pages use a light header. Native login/logout POSTs, antiforgery and OAuth flows are retained.
- Connection and configuration dialogs use a local focus trap, Escape and focus restoration. Section anchors retain
  fragment navigation, focus, sticky offsets and reduced-motion handling.

## Architecture

`Application/Data/Apps/IWorkspaceQuery.cs` defines bounded owner-scoped read models.
`Infrastructure/ApplicationServices/Data/Apps/WorkspaceQuery.cs` implements SQL projection/aggregation.
Web uses independent service scopes for overlapping read operations, cancellation and stale-response fencing.
Project entities are loaded only when the selected deployment action needs their existing configuration/scanner flow.
No Azure API, provider health check or paid AI assessment is triggered by dashboard reads.

The stylesheet `Web/wwwroot/console.css` supplies shared layouts, responsive rows and chart surfaces.
`WorkspaceOverviewPanel` owns read/refresh lifetime; `WorkspaceOverviewView` is a passive presentation component.
`ConsoleDialog` owns focus lifetime through `js/console-ui.js`. Existing telemetry charts and terminal behavior are
reused.

## Validation and local preview

Run the solution build, Web.Tests and Infrastructure.Tests. WorkspaceQueryTests also require the isolated PostgreSQL
test connection (`AUTOMATE_AI_TEST_DB`); never point verification at the application database.
Use deploy/verification/Test-WorkspacePostgres.ps1 to run them in an owned disposable Docker database.
They apply actual migrations in generated schemas and cover paging/filtering, stopped success, unknown outcomes,
missing durations, weighted samples, cross-owner reads and resource-table outages.

WorkspaceOverviewLifecycleTests covers out-of-order replies across account changes and cancellation on disposal.
ConsoleRenderingTests and TelemetryPageRenderingTests render actual components into ignored artifact directories.
Run `Web.Tests/Browser/console-ui.test.cjs` with the installed Playwright module and Edge. It covers light/dark
360/768/1440px pages for both hosting profiles, core text contrast, dialog keyboard traps, mobile navigation, section
anchors and 200% zoom.
Fixtures explicitly contain example metadata; they do not perform real OAuth, Azure deployment or Docker operations.

Screenshots are in `.artifacts/console-preview`. An earlier available Project Details screenshot is retained as
`before-details-light.png`; other older page screenshots were not available. Screenshots show fixture data, not customer
runs.

## Rollout

Restart updated Web. No new database migration, Azure setting, secret change or Telemetry deployment is needed for this
UI release. Existing consent, AI budgets, archive retention and worker behavior are unchanged.
