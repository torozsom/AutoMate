# Relocated AI policy unit tests

Retry and egress policy assertions execute in the separate [Tests/Ai/Policy](../../../../Tests/Ai/Policy/README.md)
module. This former test folder retains its navigation guide only.

```powershell
dotnet test Tests/Tests.csproj --filter FullyQualifiedName~Tests.Ai.Policy
```

- [Production policy](../../../../Application/Ai/Policy/README.md)
- [Parent guide](../README.md)
