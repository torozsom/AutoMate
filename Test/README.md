# Test

The solution's single .NET test project is `Test.csproj`. It references Application, Domain, Infrastructure, Web and
Telemetry and shares one xUnit runner and dependency set. Production dependencies and test assertions are preserved.

## Modules

- [Infra](Infra/README.md): adapters and persistence, subdivided into Auth, Data, Scanning, Ai, Azure, Diagnostics,
  GitHub, Orchestration and Templating. Infra/TestSupport contains synthetic HTTP fixtures.
- [App](App/README.md): application policies and use cases, subdivided into Ai/Policy and Orchestration.
- [Web](Web/README.md): Analysis, Diagnostics, Hosting, Metrics, Presentation and Projects, with JavaScript and Browser
  suites alongside the .NET coverage.

Namespaces begin with `Test.Infra`, `Test.App` or `Test.Web`. Add new tests to the matching business submodule;
do not create another test project. GlobalUsings.cs shares xUnit attributes and assertions across all modules.

## Verification

From the repository root:

```powershell
dotnet test Test/Test.csproj --configuration Release
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.App
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Web
node --test Test/Web/JavaScript/*.test.cjs
```

Database, Docker and external telemetry tests retain their existing explicit opt-in environment variables. Browser
checks consume previews produced by the rendering tests; see [browser verification](Web/Browser/README.md).

The repository-root `docs/` folder is local documentation, ignored by Git. Project and module READMEs remain tracked.
