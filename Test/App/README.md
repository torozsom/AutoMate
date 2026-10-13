# Application tests

Application-owned policies and cloud admission use cases live here, independent of Web component fixtures.

- `Ai/Policy/AnalysisRetryPolicyTests.cs` checks bounded backoff and provider retry timing.
- `Orchestration/CloudRuntimeConfigurationValidationTests.cs` checks cloud configuration validation and error precedence.
- `Orchestration/CloudDeploymentAdmissionTests.cs` checks profile routing and request payloads through synthetic ports.

Run `dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.App` from the repository root.
