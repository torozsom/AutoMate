# Environment scan tests

Filesystem failure distinguished from a valid empty configuration scan.

`ProjectMetadataContractTests.cs` protects discovery JSON fields, mutable provider package lists and
case-insensitive dependency package/path aggregation.

## Contents

| File                                                       | Test or fixture types      | Responsibility                                                                                     |
|------------------------------------------------------------|----------------------------|----------------------------------------------------------------------------------------------------|
| [ProjectVariableScanTests.cs](ProjectVariableScanTests.cs) | `ProjectVariableScanTests` | Verifies filesystem failures remain distinguishable from successfully scanned empty configuration. |

## Coverage and navigation

ProjectVariableScanTests invokes the real scanner boundary with a missing project directory and verifies failure remains
visible. This protects deployment forms from claiming a successful empty scan when filesystem access failed.

## Fixtures and boundaries

Use a controlled nonexistent/temporary fixture path and no external repository or cloud access. Broader parser/MSBuild
behavior is not implied by this focused case. UI merging/status behavior is covered separately in Web tests; preserve
existing exception/cancellation handling.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra.Scanning
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Extractor](../../../Infrastructure/Scanner/Environment/README.md)
- [Scanner facade](../../../Infrastructure/Scanner/Projects/README.md)
- [Form merge/feedback](../../Web/Projects/ConfigurationVariableLoadingTests.cs)
