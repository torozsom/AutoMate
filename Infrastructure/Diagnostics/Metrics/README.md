# Metric normalization and analytics

Native numeric normalization, retained sufficient statistics, scoped chart/table exploration and low-cardinality storage
metrics.

## Contents

| File                                                                       | Types or asset                     | Responsibility                                                                                      |
|----------------------------------------------------------------------------|------------------------------------|-----------------------------------------------------------------------------------------------------|
| [DeploymentMetricNormalizer.cs](DeploymentMetricNormalizer.cs)             | `DeploymentMetricNormalizer`       | Converts provider display values to explicit numeric units without filling missing values.          |
| [MetricExplorationService.cs](MetricExplorationService.cs)                 | `MetricExplorationService`         | Combines full UTC-day statistics with exact archived boundary observations without double counting. |
| [ProjectTelemetryAnalyticsService.cs](ProjectTelemetryAnalyticsService.cs) | `ProjectTelemetryAnalyticsService` | Authorizes project access before returning bounded metric projections and SQL deployment summaries. |
| [TelemetryDailyAggregationWorker.cs](TelemetryDailyAggregationWorker.cs)   | `TelemetryDailyAggregationWorker`  | Runs on the single telemetry service, not every web replica; retries replace daily results.         |
| [TelemetryStorageMetrics.cs](TelemetryStorageMetrics.cs)                   | `TelemetryStorageMetrics`          | Low-cardinality operational measurements for specialized storage.                                   |

## Usage and integration

DeploymentMetricNormalizer converts supported Docker display values to explicit units. TelemetryDailyAggregationWorker
replaces recent UTC-day observations from backend queries on the single Telemetry host. ProjectTelemetryAnalyticsService
authorizes project summaries; MetricExplorationService combines complete-day statistics with exact archived boundary
observations. TelemetryStorageMetrics emits finite operational measurements.

## Contracts and lifecycle

Never fill absent CPU/memory/provider observations with zero or infer cores from unrelated percent series. Retain
count/sum/min/max, native units and incompleteness; calculate sample-weighted means. Daily refresh preserves known prior
values but marks missing series incomplete. Observed log errors remain uncertain counts, not exact service failure
rates.

Exploration validates owner scope and absolute UTC ranges, preserves independent numeric pagination/chart coverage and
avoids double counting boundary days. Long-range summaries are not AI context windows. Aggregation retries replace
results, not append duplicated statistics; retained daily data follows project deletion rather than the former fixed-day
cleanup note.

## Related documentation

- [Parent module](../README.md)
- [Models/range rules](../../../Application/Abstractions/Diagnostics/Metrics/README.md)
- [Boundary observations](../Archive/README.md)
- [Range/paging tests](../../../Test/Infra/Diagnostics/PolicyAndMetrics/MetricExplorationTests.cs)
- [Aggregation tests](../../../Test/Infra/Diagnostics/Archive/SaaSTelemetryTests.cs)
- [Design](../../../docs/metric-exploration.md)

## Private-host state observation

TelemetrySpoolObserver owns disposable observable instruments and a background refresh. Index snapshots are obtained
under the spool gate every five seconds; disk capacity probes run every 30 seconds. Export callbacks perform no I/O or
locking. Current state and staleness have separate gauges; disk probe failures preserve prior successful observations.
TelemetryStorageMetrics also records quota rejections, segment attempt outcomes, visibility age and archive latency.
