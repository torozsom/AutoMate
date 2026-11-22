# Tests

Unit tests for isolated application policies, adapter helpers and presentation state, using .NET 11 / C# 15,
xUnit and built-in assertions. The project contains 78 test methods and 162 discovered cases, including independently
reported theory rows.
The original Docker, Templating and Scanning modules retain ten test files each.

| Module                                   | Cases | Coverage                                                                          |
|------------------------------------------|-------|-----------------------------------------------------------------------------------|
| [Docker](Docker/README.md)               | 37    | Compose parsing, process construction, stats and incremental log decoding.        |
| [Templating](Templating/README.md)       | 41    | Resource/database naming, secret identities and path containment.                 |
| [Scanning](Scanning/README.md)           | 25    | Environment parsing, JSON flattening and Git-directory names.                     |
| [Ai](Ai/README.md)                       | 15    | Retry bounds and regional egress approval rules.                                  |
| [GitHub](GitHub/README.md)               | 3     | Terminal-safe log normalization, truncation and bounded continuation.             |
| [Diagnostics](Diagnostics/README.md)     | 27    | Standalone text masking, idempotence and safe size bounds.                        |
| [Orchestration](Orchestration/README.md) | 7     | Cloud configuration validation and profile-specific admission through fake ports. |
| [Presentation](Presentation/README.md)   | 7     | Resource formatting, weighted aggregates, chart scales and replay state.          |

## Boundaries and conventions

Tests directly reference Application, Domain, Infrastructure and Web to make the production units and shared DTOs
explicit. DTOs and lightweight fake ports are allowed; provider implementations, database contexts, application hosts,
renderers, network requests and filesystem fixtures are not exercised. Referencing Web does not start the application.
The EF relational package matches Web's assembly version to avoid transitive reference conflicts; tests do not query EF.

Pure helpers continue to use their own module types and standard .NET values. Presentation tests call ordinary C#
methods rather than rendering components. Cloud-admission fakes record calls without executing deployment services.
AI unit tests own their small approval snapshot factory; shared integration fixtures remain in Test.

There is no 100-case ceiling. Equivalent input variants use xUnit theories so each row is independently reported
and a failing row does not prevent the remaining rows from executing. Preserve all input scenarios and assertions.

Classes and namespaces follow module/submodule folders. Use concise XML summaries, explicit expected values and
observable behavior assertions. No mocking library or FluentAssertions dependency is required.

## Running

```powershell
dotnet test Tests/Tests.csproj --configuration Release
dotnet test Tests/Tests.csproj --list-tests
dotnet test Tests/Tests.csproj --filter FullyQualifiedName~Tests.Ai
dotnet test Tests/Tests.csproj --filter FullyQualifiedName~Tests.Presentation
dotnet test AutoMate.slnx --configuration Release
```

Solution-based CI discovers this project automatically. TRX filenames use a prefix to keep both projects' results.
Broader integration, host, component and regression coverage remains in [Test](../Test/README.md).
