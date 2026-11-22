# Test

The solution's broad .NET 11 xUnit regression project, with Node-only JavaScript and Playwright browser suites alongside
it.
The separate [Tests project](../Tests/README.md) contains isolated helpers, application policies, fake-port
orchestration and presentation-state unit tests.
Test.csproj references Application, Domain, Infrastructure, Web and Telemetry.

Microsoft.Extensions.TimeProvider.Testing supplies test-only controllable clocks for lease-heartbeat scenarios;
production projects do not reference it. These tests advance the timer after their provider/cancellation barriers,
with wall-clock timeouts used only as deadlock guards.

Interactive component tests use the test-only bUnit dependency for dispatcher operations, disposal, DOM events and
component input assertions. Static markup tests retain HtmlRenderer. Do not subclass Blazor Renderer or inspect raw
render-tree frames/event-handler IDs; these implementation APIs trigger BL0006 and couple tests to framework internals.

## Contents

| File                               | Test or fixture types | Responsibility                                                                       |
|------------------------------------|-----------------------|--------------------------------------------------------------------------------------|
| [GlobalUsings.cs](GlobalUsings.cs) | Test setup            | Global xUnit imports shared by the .NET test modules.                                |
| [Test.csproj](Test.csproj)         | Runner configuration  | .NET 11 xUnit test project, solution references and shared test dependency versions. |

| Module                   | Coverage                                                                  |
|--------------------------|---------------------------------------------------------------------------|
| [App](App/README.md)     | Application contract regression tests; unit policies moved to Tests.      |
| [Infra](Infra/README.md) | Adapters, relational persistence, durability and provider protocol tests. |
| [Web](Web/README.md)     | Host, route, component, browser and JavaScript tests.                     |

## Coverage and navigation

Choose App for contract regressions, Infra for adapters/persistence, and Web for host/HTTP/component/browser
behavior. Portable policy, helper and state unit suites now live in Tests. Folder-aligned Test.App/Test.Infra/Test.Web
namespaces provide the filters in each guide. GlobalUsings shares
xUnit attributes/assertions; production assemblies expose selected internals to this test assembly.

## Fixtures and boundaries

Most .NET coverage uses synthetic ports/HTTP, temporary SQLite/files and in-process or ephemeral loopback hosts. With
integration gates unset, it does not invoke live Docker/cloud/LLM services. Explicit gates enable destructive fixture
operations only against isolated test resources. A skipped integration case is not evidence of external-system
correctness.

Keep broad regression tests in the matching business submodule; place isolated module tests in Tests. Preserve
assertions, fixture
ownership, public constructors and opt-in behavior. Build outputs, TestResults, node_modules and ignored preview
artifacts are generated, not source modules.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --configuration Release
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.App
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Web
node --test Test/Web/JavaScript/telemetry-chart.test.cjs Test/Web/JavaScript/xterm-wrapper.test.cjs
```

For --no-build, first build the solution in the same configuration. Playwright has separate dependencies/preview
prerequisites; follow the Browser guide.

## Execution boundaries

| Suite                           | Requirement                                                                                |
|---------------------------------|--------------------------------------------------------------------------------------------|
| AI PostgreSQL and workspace SQL | AUTOMATE_AI_TEST_DB targeting an isolated database; owned runners are linked below.        |
| Real telemetry acceptance       | AUTOMATE_TELEMETRY_TEST_DB plus the isolated Loki/Mimir endpoints expected by the fixture. |
| Real Docker/Compose smoke       | AUTOMATE_DOCKER_SMOKE=1 and a suitable already-cached image; see Docker tests.             |
| Extended spool load             | AUTOMATE_TELEMETRY_EXTENDED_LOAD=1 extends the synthetic load duration.                    |
| Playwright                      | Pinned npm dependency, selected browser and generated fixture HTML.                        |

## Source documentation

Document test scenarios/fakes/helpers with concise multiline XML contracts, including actual fixture behavior and owned
cleanup. Assertion callbacks describe expected failures, not escaping test API guarantees. Browser/Node callbacks use
JSDoc. Comments must not imply a fake supports cancellation, persistence or provider semantics that it deliberately
ignores.

## Related documentation

- [Application map](../Application/README.md)
- [Adapter map](../Infrastructure/README.md)
- [Presentation map](../Web/README.md)
- [Actual CI commands](../.github/workflows/verify.yml)
- [Isolated database runner](../deploy/verification/README.md)
