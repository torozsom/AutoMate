# Test

The solution's single .NET 10 xUnit project, with Node-only JavaScript and Playwright browser suites alongside it.
Test.csproj references Application, Domain, Infrastructure, Web and Telemetry.

## Contents

| File                               | Test or fixture types | Responsibility                                                                       |
|------------------------------------|-----------------------|--------------------------------------------------------------------------------------|
| [GlobalUsings.cs](GlobalUsings.cs) | Test setup            | Global xUnit imports shared by the .NET test modules.                                |
| [Test.csproj](Test.csproj)         | Runner configuration  | .NET 10 xUnit test project, solution references and shared test dependency versions. |

| Module                   | Coverage                                                                  |
|--------------------------|---------------------------------------------------------------------------|
| [App](App/README.md)     | Application policy and cloud-admission tests.                             |
| [Infra](Infra/README.md) | Adapters, relational persistence, durability and provider protocol tests. |
| [Web](Web/README.md)     | Host, route, component, browser and JavaScript tests.                     |

## Coverage and navigation

Choose App for portable policy/use-case tests, Infra for adapters/persistence, and Web for host/HTTP/component/browser
behavior. Folder-aligned Test.App/Test.Infra/Test.Web namespaces provide the filters in each guide. GlobalUsings shares
xUnit attributes/assertions; production assemblies expose selected internals to this test assembly.

## Fixtures and boundaries

Most .NET coverage uses synthetic ports/HTTP, temporary SQLite/files and in-process or ephemeral loopback hosts. With
integration gates unset, it does not invoke live Docker/cloud/LLM services. Explicit gates enable destructive fixture
operations only against isolated test resources. A skipped integration case is not evidence of external-system
correctness.

Keep tests in the matching business submodule rather than add another test project. Preserve assertions, fixture
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
