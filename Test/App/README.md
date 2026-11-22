# Application contract regressions

Application/data projection and legacy persisted-contract regressions remain in this project.
Pure AI policy and cloud orchestration units now execute in the separate Tests project.

| Suite                                                 | Responsibility                                                     |
|-------------------------------------------------------|--------------------------------------------------------------------|
| [ProjectionContractTests](ProjectionContractTests.cs) | Data projection and snapshot compatibility.                        |
| [LegacyContractTests](LegacyContractTests.cs)         | Persisted enum ordinals and legacy snapshot serialization linkage. |

- [Relocated AI policy](../../Tests/Ai/README.md)
- [Relocated cloud orchestration](../../Tests/Orchestration/README.md)
- [Parent test guide](../README.md)

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.App
```
