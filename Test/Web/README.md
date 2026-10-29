# Web tests

Host composition, authenticated HTTP, Blazor render/operation lifetimes, telemetry presentation and client behavior.

## Contents

| Module                                 | Coverage                                                                       |
|----------------------------------------|--------------------------------------------------------------------------------|
| [Analysis](Analysis/README.md)         | Assessment actions/polling/markup, protected HTTP endpoints and readiness.     |
| [Browser](Browser/README.md)           | Playwright checks consuming real rendered fixture HTML.                        |
| [Diagnostics](Diagnostics/README.md)   | Real SDK audit/trace exports, hub cancellation and final transport masking.    |
| [Hosting](Hosting/README.md)           | Production composition, scoped operations and platform resource/metric safety. |
| [JavaScript](JavaScript/README.md)     | Dependency-free Node chart and terminal behavior tests.                        |
| [Metrics](Metrics/README.md)           | Unit/aggregate presentation, controls, scoped reads and preview generation.    |
| [Presentation](Presentation/README.md) | Console fixtures and consistent live/history terminal formatting.              |
| [Projects](Projects/README.md)         | Owner/route/dispatcher lifetimes, project actions and saved replay.            |

## Coverage and navigation

Analysis tests shared assessment presenters/routes/probes. Diagnostics tests actual SDK exports and SignalR boundaries.
Hosting tests production DI/options/scope/resource policy. Projects tests owner/route/dispatcher/replay behavior;
Metrics/Presentation render real controls/fixtures. JavaScript and Browser have separate Node/Playwright runners.

## Fixtures and boundaries

Interactive lifecycle and event tests use bUnit public APIs; static output tests use HtmlRenderer. Both retain real
Blazor lifecycle behavior without custom Renderer subclasses or raw render-tree traversal. Other .NET fixtures use
loopback Kestrel, antiforgery or SDK serialization with synthetic
ports; they do not start live cloud/LLM/collector dependencies. Rendering copies scoped CSS from the active build
configuration, so Release-only checkouts need no Debug build. Build matching configuration before --no-build.

Preview HTML under ignored .artifacts is fixture data, not live user/deployment data. Node-only tests need no browser;
Playwright requires pinned dependencies, a browser and previews. CI keeps navigation/focus blocking and the documented
console zoom audit advisory without removing assertions.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Web
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Production map](../../Web/README.md)
- [Browser prerequisites](Browser/README.md)
- [Node-only checks](JavaScript/README.md)
- [CI selection](../../.github/workflows/verify.yml)
