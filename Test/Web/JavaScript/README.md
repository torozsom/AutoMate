# Node-only behavior tests

Dependency-free browser-module regressions using Node test/assert/VM and synthetic DOM objects.

## Contents

| File                                                 | Test or fixture types | Responsibility                                                                                         |
|------------------------------------------------------|-----------------------|--------------------------------------------------------------------------------------------------------|
| [telemetry-chart.test.cjs](telemetry-chart.test.cjs) | Browser test          | Node-only chart pointer/keyboard behavior and listener reattachment tests using synthetic DOM objects. |
| [xterm-wrapper.test.cjs](xterm-wrapper.test.cjs)     | Browser test          | Node VM tests for terminal fitting, hidden/resized viewports, history reset and disposal.              |

## Coverage and navigation

telemetry-chart.test.cjs checks pointer/keyboard sparse-observation inspection and listener replacement.
xterm-wrapper.test.cjs checks external resize coalescing, hidden terminal initialization, history reset and
disposal/reinitialization without resize feedback loops. They execute the first-party module/wrapper rather than a
copied implementation.

## Fixtures and boundaries

No Playwright install, browser, preview HTML, OAuth or Web host is needed. Fake DOM/terminal/observer state exposes
controlled behavior but does not establish real browser layout/font correctness; Playwright covers that separately. Keep
production callback/DOM contracts and fake limitations explicit.

## Running

From the repository root with Node available:

```powershell
node --test Test/Web/JavaScript/telemetry-chart.test.cjs Test/Web/JavaScript/xterm-wrapper.test.cjs
```

Explicit filenames avoid relying on shell wildcard expansion.

## Related documentation

- [Parent module](../README.md)
- [Modules under test](../../../Web/wwwroot/js/README.md)
- [Terminal owner](../../../Web/Components/Shared/Console/README.md)
- [Chart owner](../../../Web/Components/Shared/Metrics/README.md)
- [Real browser coverage](../Browser/README.md)
