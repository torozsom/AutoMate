# First-party browser behavior

Small JavaScript boundaries for theme, terminal viewport, chart inspection, dialogs/clipboard and section navigation.

## Contents

| File                                       | Types or asset | Responsibility                                                                           |
|--------------------------------------------|----------------|------------------------------------------------------------------------------------------|
| [console-ui.js](console-ui.js)             | Browser module | Visibility query, explicit clipboard copy and disposable dialog focus/keyboard handling. |
| [project-sections.js](project-sections.js) | Browser module | Disposable section scroll tracking, fragment restoration and focus navigation.           |
| [telemetry-chart.js](telemetry-chart.js)   | Browser module | Chart pointer/touch/keyboard inspection with detachable listeners/resize observer.       |
| [theme.js](theme.js)                       | Browser module | Global setTheme/getTheme/initializeTheme using data-bs-theme and localStorage.           |
| [xterm-wrapper.js](xterm-wrapper.js)       | Browser module | Global xtermWrapper instance registry, bounded viewport fitting and disposal.            |

## Usage and integration

theme.js exposes setTheme/getTheme/initializeTheme on window; the shell initializes it before first paint.
xterm-wrapper.js exposes xtermWrapper init/write/clear/dispose and expects the document's pinned xterm/Fit globals.
Other files are imported ES modules: console-ui exports visibility/copy/attachDialog, telemetry-chart exports
attach/detach, and project-sections exports attach with a disposable registration.

## Contracts and lifecycle

Razor presenters own module registrations and must dispose listeners/observers/pending animation frames/.NET references
when the selected console is replaced. xterm fitting runs only after external size changes in a coalesced frame; it must
not feed content-resize growth. Chart data is resolved from current DOM after render, not stale captured server state.

Dialog focus restores only to a connected prior target. Section scrolling respects reduced motion and scoped fragments.
Clipboard/localStorage can fail under browser policy; preserve caller handling. Theme defaults dark for
absent/unsupported stored values and uses a single data-bs-theme/localStorage contract. These helpers do not authorize
projects, fetch provider data or persist diagnostic history.

## Related documentation

- [Parent module](../README.md)
- [Terminal/dialog consumers](../../Components/Shared/Console/README.md)
- [Chart consumers](../../Components/Shared/Metrics/README.md)
- [Navigation consumer](../../Components/Shared/Projects/README.md)
- [Terminal tests](../../../Test/Web/JavaScript/xterm-wrapper.test.cjs)
- [Chart tests](../../../Test/Web/JavaScript/telemetry-chart.test.cjs)
- [Dialog tests](../../../Test/Web/Browser/console-ui.test.cjs)
- [Navigation tests](../../../Test/Web/Browser/project-sections.test.cjs)
