# Deployment configuration presentation

Editable next-launch settings and separate immutable recorded-deployment values.

## Contents

| File                                                                         | Types or asset                                     | Responsibility                                                                                            |
|------------------------------------------------------------------------------|----------------------------------------------------|-----------------------------------------------------------------------------------------------------------|
| [ConfigurationForm.razor](ConfigurationForm.razor)                           | `ConfigurationForm`                                | Edits deployment settings and merges discovered variables while retaining explicit user values.           |
| [ConfigurationForm.razor.cs](ConfigurationForm.razor.cs)                     | `ConfigurationForm`, `EnvVarItem`, `CloudDefaults` | A Blazor component that provides a user interface for configuring deployment settings for a .NET project. |
| [DeploymentConfigurationDetails.razor](DeploymentConfigurationDetails.razor) | `DeploymentConfigurationDetails`                   | Presents recorded launch settings with safe links and explicitly selected metadata copying.               |
| [RecordedValueRow.razor](RecordedValueRow.razor)                             | `RecordedValueRow`                                 | Presents a recorded metadata value and an optional browser clipboard action.                              |

## Usage and integration

ConfigurationForm binds DeploymentConfigDto, merges discovered environment variables and invokes
OnDeployConfirmed/OnCancel. Code-behind owns default-name editing, local port/form validation and Application cloud-rule
delegation. DeploymentConfigurationDetails reads saved DeploymentConfigurationSnapshot; RecordedValueRow supports
explicit value copying.

## Contracts and lifecycle

Do not mutate an admitted configuration afterward: operational requests retain the mutable reference for compatibility.
Loading discovered variables preserves explicit user values; failed extraction differs from a successful empty scan.
Credentials/environment values are secret-bearing and must not enter logs/history snapshots.

CloudRuntimeConfigurationValidation preserves existing first-error order; provider/server validation remains separate.
Recorded details use whitelisted snapshot/resource facts and show Not recorded for absent legacy history instead of
inferring current settings. Safe external links/copy actions expose only selected recorded values. Clipboard behavior
depends on browser policy.

## Related documentation

- [Parent module](../README.md)
- [Editable contracts](../../../../Domain/DTO/Deployment/README.md)
- [Recorded snapshots](../../../../Domain/Entities/Deployments/README.md)
- [Cloud rules](../../../../Application/Orchestration/Cloud/README.md)
- [Clipboard helper](../../../wwwroot/js/README.md)
- [Merge tests](../../../../Test/Web/Projects/ConfigurationVariableLoadingTests.cs)
- [Validation tests](../../../../Test/App/Orchestration/CloudRuntimeConfigurationValidationTests.cs)
- [Recorded privacy tests](../../../../Test/Web/Projects/DeploymentFailurePrivacyTests.cs)
