# Metric exploration and UI refinements

## Presentation

AI Analysis has a restrained purple border in both themes, without animated emphasis. Deployment Details places the
runtime badge beside its short ID; recorded outcome remains in overview. GitHub/local source pages restore their
previous card layout and expanded local imports while retaining the shared theme. Azure identity and deployment-dialog
footer spacing is 16px.

## Ranges and aggregation

All chart groups offer 10m, 30m, 1h, 6h, 24h, 7d, 30d, 90d, 1y and 5y presets, plus Custom UTC.
Custom windows span at least five minutes and at most five calendar years, measured backward from their end, and cannot
end in the future. This definition handles leap dates consistently with five-year presets. Windows are half-open:
start inclusive, end exclusive. Historical lifetime clips to the most recent five years with a notice; very recent
deployments use a five-minute observation window.

Overview and Project Analytics default to 30 days; Deployment Details defaults to recorded lifetime. Overview advances
relative bounds when its existing visible 30-second refresh runs; custom bounds remain frozen. Picker changes cancel
obsolete reads. Charts and statistics refer to the displayed response's frozen range, not a pending selection.

Short windows use archived observations. Longer windows use persisted summaries for complete UTC dates and exact
archived evidence for partial first/last dates. No whole daily average is substituted into a ten-minute range.
Minute/day/Monday-week/calendar-month buckets keep each series at most 240 points. Partial edge buckets are clipped to
the selected bounds. Missing observations remain gaps; no data before collection started or expired legacy diagnostics
can be recreated.

Observed sums/counts produce sample-weighted averages. Per-container/deployment identity, units and extrema survive
aggregation. Raw observations take precedence over imported backend copies in the same bucket. Imported interval means
have a distinct interval denominator and never claim raw sample counts; population means use raw samples when available
and clearly disclose imported evidence. Coverage reports actual samples and observed aggregation buckets, never expected
sampling coverage or health thresholds.

Numeric metric and outcome tables default to 25 rows, with 10/25/50 choices. Each table has independent page state.
Pagination never restricts charts. Configuration key/value tables remain unpaginated.

## Read boundaries and recovery

IMetricExploration accepts owner, optional project/deployment/container, absolute range and statistics pagination.
IWorkspaceQuery accepts absolute ranges as well as its existing compatibility overload. The private metric-batch route
resolves current membership and managed-storage consent, selects ten partitions per request and returns at most 4,000
aggregates with continuation. Web sends bounded batches, not a request per project and never an Azure provider request.

The archive's metric-index contains day-partitioned checksummed references and completion metadata, without log prose.
Reads verify original source checksums. A sealed checksummed manifest detects missing lookup references. Legacy lookup
recovery processes at most 2,000 segments per query and marks results partial until complete; refresh continues
recovery.
Lookup corruption removes only derived files, never acknowledged source segments. Project/account cleanup removes the
lookup together with the archived partition.

Queries stream at most 250,000 filtered daily rows and cap aggregation state at 100,000 scoped rows, with explicit
partial guidance. Private archive partition aggregation
caps its state at 20,000 rows, and batch output at 4,000 to remain within the existing 8 MiB transport bound. For
unusually
large scopes select a project/container. Archive outages/timeouts preserve usable full-day summaries and disclose
partial
boundary evidence. Daily-summary outages preserve available boundary observations and disclose missing full days.

## Rollout and operations

No database migration, Azure configuration, secrets or AI accounting changes are required. Deploy updated private
Telemetry before updated Web. Preserve the existing encrypted persistent archive volume and its backups; derived lookups
are optional to restore and can be regenerated, while original segments must be backed up. Lookup files increase volume
usage alongside retained history, so monitor disk growth. An older/unavailable host produces actionable partial
guidance.

AI context windows, consent controls, pricing, disabled retries and saved assessments retain their existing behavior.
The five-year limits apply to chart exploration only.

## Verification

Range tests cover leap dates, custom minimum/future bounds and maximum chart cardinality. Archive tests cover duplicate
samples, container selection, restart, lookup corruption and deletion. Disposable PostgreSQL checks cover owner/consent
denial, filtered pagination, full UTC days versus exact boundaries, five-year reads and backend outages. Real Blazor
renderer events verify page/size changes preserve chart evidence. Browser fixtures identify their synthetic provider
data explicitly and capture both themes at 360/768/1440px, section navigation and 200% zoom.
