# Templating

Scriban deployment-artifact generation infrastructure adapter.
Artifact logs retain reviewed deployment filenames without exposing full output paths or file contents.
An `.azurecr.io` registry renders the Azure OIDC/ACR workflow and a managed-identity Container App registry entry.
The legacy GHCR template remains available for self-hosted deployments.

## Source inventory

- `Models/CloudDeploymentSecretNames.cs`
- `Rendering/ScribanTemplateRenderer.cs`
- `Rendering/TemplateFileWriter.cs`
- `Catalog/TemplateManifestCatalog.cs`
- `Models/TemplateModelFactory.cs`
- `Models/TemplateNameNormalizer.cs`
- `Catalog/TemplatePaths.cs`
- `Catalog/TemplateRuleMatcher.cs`
- `Rendering/TemplatingService.cs`

## Boundary

Infrastructure may reference Application and Domain, but never Web. It implements ports and owns provider-specific
behavior.

## Related documentation

- [Solution navigation map](../../navigation.md)

Local Compose assets include AutoMate ownership labels. Web uses `AUTOMATE_DEPLOYMENT_ID`, supplied to the Compose
process
by DockerCli; database labels remain project/service scoped so each deployment ID does not force database recreation.
Existing container/service names, ports, images and volume behavior are preserved. `LocalOwnershipTemplateTests` guards
these conventions. Cloud templates do not consume this local deployment environment variable.

Cloud managed database resources now receive project/Container App ownership tags and an `automate-runtime` diagnostic
setting exporting operational categories to the deployment workspace using AzureDiagnostics: Cosmos `MongoRequests`,
PostgreSQL `PostgreSQLLogs`, and SQL `Errors`, `Timeouts`, `Deadlocks`. MySQL and Redis default to metrics only.
Query text, audit, connection and slow-query logging remain opt-in. SQL telemetry is scoped to the database resource.
Metrics are
read directly through ARM. Regenerate and deploy the Bicep for existing installations to obtain database collection;
provider-specific logging/auditing flags remain customer controlled. Diagnostic export requires Azure permissions and
may incur Azure Monitor ingestion charges. No credentials appear in discovery tags.

Both registry workflow variants start the app after infrastructure deployment. Bicep updates can retain a prior
resource-level Stopped state; the explicit start resumes it without changing scale limits or network policy.

## Folder organization

Folders group related files while retaining the module's existing .NET namespaces and public contracts.

- `Catalog/` — `Catalog/TemplateManifestCatalog.cs`, `Catalog/TemplatePaths.cs`, `Catalog/TemplateRuleMatcher.cs`.
- `Models/` — `Models/CloudDeploymentSecretNames.cs`, `Models/TemplateModelFactory.cs`,
  `Models/TemplateNameNormalizer.cs`.
- `Rendering/` — `Rendering/ScribanTemplateRenderer.cs`, `Rendering/TemplateFileWriter.cs`,
  `Rendering/TemplatingService.cs`.
