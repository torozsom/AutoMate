# Cloud policy unit tests

| Suite                                                                                   | Verifies                                                                           |
|-----------------------------------------------------------------------------------------|------------------------------------------------------------------------------------|
| [CloudRuntimeConfigurationValidationTests](CloudRuntimeConfigurationValidationTests.cs) | First-error precedence, unchanged inputs and environment-name constraints.         |
| [CloudDeploymentAdmissionTests](CloudDeploymentAdmissionTests.cs)                       | Both profiles preserve the request and call only the selected fake admission port. |

Migrated from Test while retaining all assertion scenarios. Parameterized inputs are independently reported
as xUnit theory cases. See the [parent module](../README.md) and
[project boundaries](../../README.md).
