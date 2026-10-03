# Templating

Scriban deployment-artifact generation infrastructure adapter.
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
