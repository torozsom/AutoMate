# Deployment template tests

Rendered local ownership and Azure registry/workflow/operational-diagnostic contracts.

## Contents

| File                                                             | Test or fixture types         | Responsibility                                                                   |
|------------------------------------------------------------------|-------------------------------|----------------------------------------------------------------------------------|
| [CloudRegistryTemplateTests.cs](CloudRegistryTemplateTests.cs)   | `CloudRegistryTemplateTests`  | Guards the SaaS ACR path against reintroducing long-lived GHCR pull credentials. |
| [LocalOwnershipTemplateTests.cs](LocalOwnershipTemplateTests.cs) | `LocalOwnershipTemplateTests` | Guards generated ownership metadata and existing database/container behavior.    |

## Coverage and navigation

LocalOwnershipTemplateTests renders Compose and checks labels without altering container names, ports or database
lifetime. CloudRegistryTemplateTests renders ACR/OIDC assets, confirms no GHCR PAT dependency on that path, checks
explicit app start and owned database diagnostic settings.

## Fixtures and boundaries

Tests render repository templates with synthetic configuration; they do not run Docker, commit workflows or deploy Azure
resources. Keep project-root/runtime asset discovery and selected build configuration available. Change model, secret
naming, manifest destinations and templates together; successful rendering is not provider acceptance.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra.Templating
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Generation map](../../../Infrastructure/Templating/README.md)
- [Runtime assets](../../../Infrastructure/Templating/Templates/README.md)
- [Ownership/export consumers](../../../Infrastructure/Azure/Databases/README.md)

TemplateManifestContractTests verifies JSON output destinations plus disabled/local/cloud artifact selection,
including whitespace-normalized target values.
