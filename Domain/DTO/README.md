# DTO

Shared data-transfer records used by application workflows and adapters.

## Source inventory

- `Azure/AzureCloudCredentialsDto.cs`
- `Azure/AzureContainerAppRuntimeStreamRequest.cs`
- `Azure/AzureOidcSetupResultDto.cs`
- `Deployment/CloudDeploymentRequestDto.cs`
- `Projects/CsProjectDto.cs`
- `Deployment/DatabaseConfigDto.cs`
- `Projects/DbProviderRuleDto.cs`
- `Deployment/DeploymentConfigDto.cs`
- `GitHub/GitHubRepositoryDto.cs`
- `GitHub/GitHubWorkflowRunDto.cs`
- `GitHub/GitHubWorkflowJobDto.cs`
- `Projects/LocalProjectDto.cs`
- `Projects/ProjectMetadataDto.cs`
- `Templating/TemplateFile.cs`
- `Templating/TemplateManifestRuleDto.cs`

## Boundary

Keep this module independent of Application, Infrastructure, Web, framework APIs, and provider SDKs.

## Related documentation

- [Solution navigation map](../../navigation.md)

## Folder organization

Folders group related files while retaining the module's existing .NET namespaces and public contracts.

- `Azure/` — `Azure/AzureCloudCredentialsDto.cs`, `Azure/AzureContainerAppRuntimeStreamRequest.cs`,
  `Azure/AzureOidcSetupResultDto.cs`.
- `Deployment/` — `Deployment/CloudDeploymentRequestDto.cs`, `Deployment/DatabaseConfigDto.cs`,
  `Deployment/DeploymentConfigDto.cs`.
- `GitHub/` — `GitHub/GitHubRepositoryDto.cs`, `GitHub/GitHubWorkflowJobDto.cs`, `GitHub/GitHubWorkflowRunDto.cs`.
- `Projects/` — `Projects/CsProjectDto.cs`, `Projects/LocalProjectDto.cs`, `Projects/ProjectMetadataDto.cs`,
  `Projects/DbProviderRuleDto.cs`.
- `Templating/` — `Templating/TemplateFile.cs`, `Templating/TemplateManifestRuleDto.cs`.
