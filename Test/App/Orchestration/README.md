# Relocated cloud orchestration unit tests

Both suites now execute in [Tests/Orchestration/Cloud](../../../Tests/Orchestration/Cloud/README.md).
DTO construction and fake admission ports verify policy without running deployment services.

- [CloudDeploymentAdmissionTests](../../../Tests/Orchestration/Cloud/CloudDeploymentAdmissionTests.cs)
- [CloudRuntimeConfigurationValidationTests](../../../Tests/Orchestration/Cloud/CloudRuntimeConfigurationValidationTests.cs)
- [Production cloud module](../../../Application/Orchestration/Cloud/README.md)
- [Parent guide](../README.md)

```powershell
dotnet test Tests/Tests.csproj --filter FullyQualifiedName~Tests.Orchestration.Cloud
```
