# Domain

Framework-neutral business entities, enums, DTOs, and defaults.

## Source inventory

- `Domain.csproj`

## Boundary

Keep this module independent of Application, Infrastructure, Web, framework APIs, and provider SDKs.

## Related documentation

- [Application](../Application/README.md)
- [Infrastructure](../Infrastructure/README.md)
- [Web](../Web/README.md)

## Deployment history update (2026-10-08)

DeploymentOutcome records preparation success/failure separately from Running/Stopped.
AiDeploymentAnalysis.RetainUntilDeleted separates saved-result lifetime from finite execution expiry.

## Source organization

See the [solution folder map](../navigation.md#responsibility-folders) for module subdivisions. Folder moves preserve
existing .NET namespaces and public contracts. Generated build outputs and third-party assets retain their standard
layouts.

## XML documentation

Use concise multiline XML summaries for types, constructors and methods, including nested declarations. Document all
positional-record and constructor parameters. Fields, properties and enum members may use single-line summaries.
Describe nullability, units, lifecycle, ownership and sensitive values where they affect the contract; do not imply that
DTOs enforce validation, authorization or secure storage themselves. Add return and exception tags only where an actual
callable contract requires them. Documentation changes must preserve executable tokens, defaults and persisted enum
values.
