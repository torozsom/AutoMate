# Metric and analytics contracts

Typed numeric samples, log/metric storage ports, owner-authorized history, frozen chart windows and aggregate read
models.

## Contents

| File                                                         | Types                                                                                                                                                                                                                                                                                                                                  | Responsibility                                                                                                   |
|--------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------|
| [DeploymentTelemetry.cs](DeploymentTelemetry.cs)             | `DeploymentMetricSample`, `DeploymentMetricCatalog`, `DeploymentRuntimeMetricSnapshot`, `DeploymentLogEnvelope`, `IDeploymentLogWriter`, `IDeploymentLogQuery`, `IDeploymentMetricWriter`, `DeploymentMetricPoint`, `DeploymentMetricHistory`, `IDeploymentMetricQuery`, `IDeploymentHistoryService`, `DeploymentTelemetryPreferences` | Typed metrics/catalog, live observations, durable envelopes, backend write/query ports and owner-facing history. |
| [MetricExploration.cs](MetricExploration.cs)                 | `MetricTimeRange`, `MetricExplorationQuery`, `MetricObservation`, `MetricExplorationResult`, `IMetricExploration`, `ArchiveMetricBatchRequest`, `ArchiveMetricBatch`                                                                                                                                                                   | Frozen UTC range validation/bucketing, sufficient-statistic observations and independent chart/table paging.     |
| [ProjectTelemetryAnalytics.cs](ProjectTelemetryAnalytics.cs) | `DailyMetricStatistics`, `IDailyDeploymentMetricQuery`, `IDeploymentErrorCountQuery`, `DeploymentAnalyticsRow`, `ProjectTelemetryAnalytics`, `IProjectTelemetryAnalytics`                                                                                                                                                              | Daily sufficient statistics, observed error queries and owner-scoped project analytics.                          |

## Usage and integration

DeploymentTelemetry defines samples/catalog, live snapshots, durable envelopes, separate log/metric write/query ports
and IDeploymentHistoryService. MetricExploration defines frozen ranges, per-observation sufficient statistics,
independent chart/table results and private archive batches. ProjectTelemetryAnalytics adds daily statistics,
error-count queries and project summaries. Infrastructure implements persistence, backend reads and analytics.

## Contracts and lifecycle

Keep metric names/units within DeploymentMetricCatalog and preserve source timestamp/resource/component identity on live
snapshots. History preferences remain compatibility APIs; current runtime collection/approved storage policy is
automatic and separate from AI consent. The default ReadLogsV2Async rejects channel filtering explicitly; real adapters
implement pre-limit filtering and owner checks.

MetricTimeRange.Validate accepts absolute UTC windows from five minutes through five calendar years ending no later than
now. Preset creates ranges; callers still validate. Buckets adapt from minute intervals to UTC days/weeks/calendar
months. Chart and numeric page projections share a frozen window but paginate independently. These limits are separate
from AI assessment windows/budgets.

MetricObservation retains Samples/Sum/Min/Max plus incompleteness and imported interval count; Average uses sample
weighting or the import fallback. Daily sufficient statistics should not be averaged as unweighted averages of averages.
Observed error counts do not establish a service-wide failure rate. Preserve availability, missing values and incomplete
flags rather than invent zeros.

Older query defaults can throw for selected log reads or filter bounded metric reads after retrieval; focused assessment
archive support is the stronger contract. Owner-facing services must authorize scope before trusted backend ports are
used.

## Related documentation

- [Parent module](../README.md)
- [Exploration implementation](../../../../Infrastructure/Diagnostics/Metrics/MetricExplorationService.cs)
- [Project analytics](../../../../Infrastructure/Diagnostics/Metrics/ProjectTelemetryAnalyticsService.cs)
- [History/query adapter](../../../../Infrastructure/Diagnostics/Storage/DeploymentTelemetryStore.cs)
- [Private metric batches](../../../../Infrastructure/Diagnostics/Archive/ArchiveMetricBatchReader.cs)
- [Range and pagination tests](../../../../Test/Infra/Diagnostics/PolicyAndMetrics/MetricExplorationTests.cs)
- [Analytics tests](../../../../Test/Infra/Diagnostics/Archive/SaaSTelemetryTests.cs)
- [Metric exploration design](../../../../docs/metric-exploration.md)
