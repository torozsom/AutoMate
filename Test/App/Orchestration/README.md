# Cloud admission and validation tests

Application-port routing and existing form-rule/error precedence.

## Contents

| File                                                                                       | Test or fixture types                        | Responsibility                                                                                       |
|--------------------------------------------------------------------------------------------|----------------------------------------------|------------------------------------------------------------------------------------------------------|
| [CloudDeploymentAdmissionTests.cs](CloudDeploymentAdmissionTests.cs)                       | `CloudDeploymentAdmissionTests`, `PortProxy` | Verifies cloud admission requests at application ports rather than component implementation details. |
| [CloudRuntimeConfigurationValidationTests.cs](CloudRuntimeConfigurationValidationTests.cs) | `CloudRuntimeConfigurationValidationTests`   | Characterizes the existing cloud form policy independently of private component members.             |

## Coverage and navigation

CloudDeploymentAdmissionTests records SelfHosted queue versus SaaS run-service calls and verifies request payloads.
CloudRuntimeConfigurationValidationTests preserves database-before-environment error order and existing environment-name
constraints.

## Fixtures and boundaries

Use synthetic capabilities/ports/configuration; no Web renderer, database or provider access is involved. These tests
protect Application behavior, not repository permission, Azure provisioning or template correctness. Keep request
data/first-error semantics compatible when refining the UI boundary.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.App.Orchestration
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Admission/rules](../../../Application/Orchestration/Cloud/README.md)
- [Generated artifacts](../../Infra/Templating/README.md)
- [Connection preparation](../../Web/Projects/ConnectedCloudDeploymentAdmissionTests.cs)
