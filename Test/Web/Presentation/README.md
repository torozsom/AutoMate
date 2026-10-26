# Console rendering and output tests

Real console surfaces/preview artifacts and consistent live/history terminal text.

## Contents

| File                                                                             | Test or fixture types                                                                                                                                                                                                                                                                   | Responsibility                                                                          |
|----------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------|
| [ConsoleRenderingTests.cs](ConsoleRenderingTests.cs)                             | `ConsoleRenderingTests`, `PreviewCapabilities`, `PreviewAuthentication`, `PreviewQuery`, `PreviewRoot`, `PreviewProjects`, `PreviewAzure`, `PreviewGitHub`, `PreviewLocal`, `PreviewConfiguration`, `PreviewVerify`, `EmptyPort`, `PreviewNavigation`, `StaticJs`, `PreviewAntiforgery` | Real component rendering, shared preview fakes and console HTML/CSS fixture generation. |
| [DeploymentTerminalPresentationTests.cs](DeploymentTerminalPresentationTests.cs) | `DeploymentTerminalPresentationTests`                                                                                                                                                                                                                                                   | Checks equal live/history text treatment without relying on terminal colors.            |

## Coverage and navigation

ConsoleRenderingTests renders actual pages/shared controls with declared fixture ports and writes
.artifacts/console-preview HTML plus the matching scoped CSS. Its nested fakes support other rendering suites.
DeploymentTerminalPresentationTests checks stderr visibility, Azure source timestamps/multiline formatting, operational
database rows and suppression of mixed utilization summaries.

## Fixtures and boundaries

Fixtures are metadata snapshots, not live deployments/authentication. Some fake ports intentionally reject calls so
static rendering cannot unexpectedly perform external work. Use the build configuration's scoped CSS rather than depend
on Debug output from a Release run. Text formatting must not rewrite saved event/cursor identity or invent missing
timestamps; browser colors/layout are independently verified.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Web.Presentation
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Terminal presentation](../../../Web/Components/Shared/Console/README.md)
- [Console pages](../../../Web/Components/Pages/Workspace/README.md)
- [Shared preview consumers](../Metrics/TelemetryPageRenderingTests.cs)
- [Layout checks](../Browser/README.md)
