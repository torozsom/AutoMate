# Metric presentation tests

Recorded unit/aggregate semantics, real controls/charts, asynchronous scope fencing and preview generation.

## Contents

| File                                                                         | Test or fixture types                                                                                                                                                                                                     | Responsibility                                                                                         |
|------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------|
| [DeploymentMetricDisplayTests.cs](DeploymentMetricDisplayTests.cs)           | `DeploymentMetricDisplayTests`                                                                                                                                                                                            | Verifies restored metric cards preserve numeric units and missing observations.                        |
| [MetricExplorationInteractionTests.cs](MetricExplorationInteractionTests.cs) | `MetricExplorationInteractionTests`, `FixtureJs`                                                                                                                                                       | Checks actual pagination events and preservation of complete chart input.                              |
| [MetricExplorationRenderingTests.cs](MetricExplorationRenderingTests.cs)     | `MetricExplorationRenderingTests`                                                                                                                                                                                         | Real controls and numeric alternatives preserve UTC validation and independent pagination.             |
| [ProjectDetailsMetricRecoveryTests.cs](ProjectDetailsMetricRecoveryTests.cs) | `ProjectDetailsMetricRecoveryTests`, `HistoryProxy`, `RecordingLogger`                                                                                                                                                    | Verifies that pending metric reads cannot update a closed page or report expected disposal as failure. |
| [ProjectTelemetrySummaryTests.cs](ProjectTelemetrySummaryTests.cs)           | `ProjectTelemetrySummaryTests`, `ScopeRecorder`, `ScopedAnalytics`                                                                                                                                                        | Analytics queries must not share the parent Blazor circuit's scoped database services.                 |
| [TelemetryChartRenderingTests.cs](TelemetryChartRenderingTests.cs)           | `TelemetryChartRenderingTests`, `NoBrowserJs`                                                                                                                                                                             | Checks the rendered chart for sparse and missing observations, not just its calculations.              |
| [TelemetryPageRenderingTests.cs](TelemetryPageRenderingTests.cs)             | `TelemetryPageRenderingTests`, `FixtureProject`, `FixtureDetails`, `EmptyPort`, `FixtureNavigation`, `FixtureAuthentication`, `UnusedUserService`, `FixtureAssessments`, `StaticJs`, `FixtureHistory`, `FixtureAnalytics` | Provider-free real telemetry/current/history rendering and metrics-preview generation.                 |
| [TelemetryPresentationTests.cs](TelemetryPresentationTests.cs)               | `TelemetryPresentationTests`                                                                                                                                                                                              | Resource display must retain small values and actual sample weighting.                                 |

## Coverage and navigation

TelemetryPresentation tests small/native values, sample-weighted daily points and sparse axes. Chart/Range/Statistics
tests render actual controls and exercise real callbacks for frozen UTC ranges and independent pagination.
ProjectTelemetrySummary tests overlapping scoped reads; ProjectDetailsMetricRecovery tests disposal/older observation
fences. PageRendering generates real current/history fixture HTML.

## Fixtures and boundaries

Controlled metrics/ports and HtmlRenderer need no provider/OAuth. MetricExplorationInteraction uses bUnit to click the
actual pager buttons and change the page-size select, checks rendered tbody rows, and inspects TelemetryChart inputs
through typed component queries. It does not read internal render-tree frames or dispatch raw event-handler IDs. Do not fill gaps with zero or limit chart evidence to
a numeric table page. Chart JS pointer/keyboard behavior is a separate Node suite; browser layout is separate Playwright
coverage. Generated .artifacts/metrics-preview pages are clearly synthetic, with scoped CSS resolved from the selected
build configuration.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Web.Metrics
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Production controls](../../../Web/Components/Shared/Metrics/README.md)
- [Recovery owner](../../../Web/Components/Pages/Projects/README.md)
- [Client inspection tests](../JavaScript/README.md)
- [Preview consumption](../Browser/README.md)
- [Range/data contracts](../../../Application/Abstractions/Diagnostics/Metrics/README.md)
