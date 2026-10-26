# Observed telemetry controls

Frozen UTC ranges, numeric alternatives, accessible SVG charts and presentation-only metric formatting.

## Contents

| File                                                                 | Types or asset                                 | Responsibility                                                                                    |
|----------------------------------------------------------------------|------------------------------------------------|---------------------------------------------------------------------------------------------------|
| [MetricRangePicker.razor](MetricRangePicker.razor)                   | `MetricRangePicker`                            | Selects relative or explicit frozen UTC windows and validates range bounds before publication.    |
| [MetricRangePicker.razor.cs](MetricRangePicker.razor.cs)             | `MetricRangePicker` partial class              | Component state, callbacks and lifecycle behavior.                                                |
| [MetricStatisticsView.razor](MetricStatisticsView.razor)             | `MetricStatisticsView`                         | Presents metric charts and an independent paginated numeric alternative from recorded aggregates. |
| [MetricStatisticsView.razor.cs](MetricStatisticsView.razor.cs)       | `MetricStatisticsView` partial class           | Component state, callbacks and lifecycle behavior.                                                |
| [ProjectTelemetrySummary.razor](ProjectTelemetrySummary.razor)       | `ProjectTelemetrySummary`                      | Loads and presents owner-scoped project aggregates for the selected metric window.                |
| [ProjectTelemetrySummary.razor.cs](ProjectTelemetrySummary.razor.cs) | `ProjectTelemetrySummary` partial class        | Component state, callbacks and lifecycle behavior.                                                |
| [StatisticsPager.razor](StatisticsPager.razor)                       | `StatisticsPager`                              | Presents bounded numeric-table paging and supported row-count selection.                          |
| [StatisticsPager.razor.cs](StatisticsPager.razor.cs)                 | `StatisticsPager` partial class                | Component state, callbacks and lifecycle behavior.                                                |
| [TelemetryChart.razor](TelemetryChart.razor)                         | `TelemetryChart`                               | Presents observed SVG metric points with accessible pointer and keyboard inspection.              |
| [TelemetryChart.razor.cs](TelemetryChart.razor.cs)                   | `TelemetryChart` partial class                 | Component state, callbacks and lifecycle behavior.                                                |
| [TelemetryPreferences.razor](TelemetryPreferences.razor)             | `TelemetryPreferences`                         | Presents authorized automatic-collection, retention and sampling information.                     |
| [TelemetryPreferences.razor.cs](TelemetryPreferences.razor.cs)       | `TelemetryPreferences` partial class           | Component state, callbacks and lifecycle behavior.                                                |
| [TelemetryPresentation.cs](TelemetryPresentation.cs)                 | `TelemetryPresentation`, `TelemetryChartPoint` | Friendly units/labels, observed chart aggregates and TelemetryChartPoint projections.             |

## Usage and integration

MetricRangePicker creates/validates absolute windows; TelemetryChart renders supplied points and uses telemetry-chart.js
for client inspection. MetricStatisticsView and StatisticsPager provide independent numeric paging.
ProjectTelemetrySummary performs scoped owner-authorized aggregate reads. TelemetryPreferences presents automatic
collection/retention information. TelemetryPresentation supplies labels/units/observed chart projections.

## Contracts and lifecycle

Windows span supported ten-minute through five-year presets; custom ranges use Application validation with a five-minute
minimum. Range changes freeze start/end and must not reset log channel/cursors. Numeric tables offer independent
10/25/50-row pages. Cancel/version overlapping reads so older ranges cannot replace the selected one.

Never synthesize missing measurements or connect gaps as continuous observations. Preserve isolated points and native
unit meaning; memory capacity is supporting information rather than a scale-flattening usage line. Daily summaries use
sample weighting; historical returned interval averages lack raw counts and retain that limitation.

Chart inspection supports pointer/touch/arrows without server roundtrips. Attach after render and detach
listeners/resize observers/.NET references at disposal. Availability is separate from empty/zero values; automatic
collection is distinct from explicit AI egress consent.

## Related documentation

- [Parent module](../README.md)
- [Range/data contracts](../../../../Application/Abstractions/Diagnostics/Metrics/README.md)
- [Inspection module](../../../wwwroot/js/telemetry-chart.js)
- [Formatting tests](../../../../Test/Web/Metrics/TelemetryPresentationTests.cs)
- [Range/paging tests](../../../../Test/Web/Metrics/MetricExplorationInteractionTests.cs)
- [Chart tests](../../../../Test/Web/Metrics/TelemetryChartRenderingTests.cs)
- [JS disposal tests](../../../../Test/Web/JavaScript/telemetry-chart.test.cjs)
- [Metric design](../../../../docs/metric-exploration.md)
