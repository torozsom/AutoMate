# Reusable presenters

Analysis, deployment settings, terminal/dialog, metrics and workspace/project presentation components.

## Contents

| Module                                   | Responsibility                                                            |
|------------------------------------------|---------------------------------------------------------------------------|
| [Analysis](Analysis/README.md)           | Assessment choices, consent, result management and safe answer rendering. |
| [Configuration](Configuration/README.md) | Next-launch editing and immutable recorded-value presentation.            |
| [Console](Console/README.md)             | Terminal, modal, theme and source-aware output formatting.                |
| [Metrics](Metrics/README.md)             | Frozen ranges, charts, numeric paging and observed units.                 |
| [Projects](Projects/README.md)           | Status/workflow badges, repository parsing and section navigation.        |
| [Workspace](Workspace/README.md)         | Owner-scoped overview, KPI and inventory presenters.                      |

## Usage and integration

Choose a child module by behavior. Page containers supply owned IDs/state and actions; reusable views render data or
invoke focused scoped Application reads. Keep provider/EF implementations outside shared controls. Existing Razor
namespaces may differ from folder names; use declarations/imports as the source of truth.

## Contracts and lifecycle

Callbacks delegate ownership-sensitive mutations to authorized workflows. Respect cancellation, frozen windows, nullable
observations and JS disposal. Render untrusted/provider-authored text as text, not markup. Distinguish next-launch
configuration from immutable recorded history and live snapshots from sampled/aggregated metrics.

## Related documentation

- [Parent module](../README.md)
- [Page owners](../Pages/README.md)
- [Independent operations](../../Services/README.md)
- [Contracts](../../../Application/README.md)
- [Presentation checks](../../../Test/Web/Presentation/ConsoleRenderingTests.cs)
