# Playwright fixture verification

Real browser layout, local fonts/theme, dialog focus and project section navigation against rendered component fixtures.

## Contents

| File                                                   | Test or fixture types | Responsibility                                                                                      |
|--------------------------------------------------------|-----------------------|-----------------------------------------------------------------------------------------------------|
| [console-ui.test.cjs](console-ui.test.cjs)             | Browser test          | Loopback Playwright layout/theme/fonts/dialog/zoom checks against rendered console previews.        |
| [package-lock.json](package-lock.json)                 | Runner configuration  | Exact browser-test dependency graph for npm ci; production packages are separate.                   |
| [package.json](package.json)                           | Runner configuration  | Pinned Playwright dependency and serial Node test command.                                          |
| [project-sections.test.cjs](project-sections.test.cjs) | Browser test          | Loopback Playwright fragment/section/focus/dialog checks against rendered project/history previews. |

## Coverage and navigation

console-ui.test.cjs serves .artifacts/console-preview pages/local assets and checks responsive/theme/font/dialog/zoom
behavior. project-sections.test.cjs serves .artifacts/metrics-preview project/history fixtures and checks section order,
fragments, focus and analysis dialogs. Both use ephemeral loopback servers and the pinned Playwright dependency, not a
live AutoMate login/provider.

## Fixtures and boundaries

Generate previews with the real .NET rendering tests first. Build the solution/test project in the same configuration
used for --no-build; CSS comes from that configuration. Ignored artifacts/node_modules/browser caches are outputs, not
source modules. Browser channel defaults to msedge; CI selects chromium. AUTOMATE_PLAYWRIGHT_MODULE is an optional
module-resolution override, not a production setting.

CI downloads console-preview/metrics-preview artifacts from the .NET test job instead of rebuilding the solution in
the browser job. The artifact includes generated scoped CSS and Bootstrap copies produced by the rendering tests;
maintained static assets come from the checked-out source. CI keeps project-section checks blocking and marks the
console audit advisory because of documented repository-page
overflow at 200% zoom. npm test retains all assertions and can fail on that audit; no passing live-browser result is
implied by installing dependencies or rendering previews.

## Running

Run from the repository root (PowerShell):

```powershell
npm ci --prefix Test/Web/Browser
Push-Location Test/Web/Browser
npx playwright install chromium
Pop-Location
dotnet test Test/Test.csproj --filter "FullyQualifiedName~ConsoleRenderingTests|FullyQualifiedName~TelemetryPageRenderingTests"
$env:AUTOMATE_BROWSER_CHANNEL = 'chromium'
node --test Test/Web/Browser/project-sections.test.cjs
npm --prefix Test/Web/Browser test
```

The first browser command isolates navigation/focus; npm test runs both scripts serially. Linux CI installs Chromium's
system dependencies with Playwright --with-deps. Node-only chart/xterm tests need none of these browser prerequisites.

## Related documentation

- [Parent module](../README.md)
- [Console fixture producer](../Presentation/README.md)
- [Metric/project fixture producer](../Metrics/README.md)
- [Browser modules](../../../Web/wwwroot/js/README.md)
- [Actual CI/advisory configuration](../../../.github/workflows/verify.yml)
