# Templating

Scriban deployment-artifact generation infrastructure adapter.
Artifact logs retain reviewed deployment filenames without exposing full output paths or file contents.
An `.azurecr.io` registry renders the Azure OIDC/ACR workflow and a managed-identity Container App registry entry.
The legacy GHCR template remains available for self-hosted deployments.

## Source inventory

- `CloudDeploymentSecretNames.cs`
- `ScribanTemplateRenderer.cs`
- `TemplateFileWriter.cs`
- `TemplateManifestCatalog.cs`
- `TemplateModelFactory.cs`
- `TemplateNameNormalizer.cs`
- `TemplatePaths.cs`
- `TemplateRuleMatcher.cs`
- `TemplatingService.cs`

## Boundary

Infrastructure may reference Application and Domain, but never Web. It implements ports and owns provider-specific
behavior.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)

Local Compose assets include AutoMate ownership labels. Web uses `AUTOMATE_DEPLOYMENT_ID`, supplied to the Compose
process
by DockerCli; database labels remain project/service scoped so each deployment ID does not force database recreation.
Existing container/service names, ports, images and volume behavior are preserved. `LocalOwnershipTemplateTests` guards
these conventions. Cloud templates do not consume this local deployment environment variable.
