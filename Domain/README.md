# Domain

Framework-neutral business entities, enums, DTOs, and defaults.

## Source inventory

- `Domain.csproj`

## Boundary

Keep this module independent of Application, Infrastructure, Web, framework APIs, and provider SDKs.

## Related documentation

- [Application](../Application/README.md)`n- [Infrastructure](../Infrastructure/README.md)`n- [Web](../Web/README.md)

## Deployment history update (2026-10-08)

DeploymentOutcome records preparation success/failure separately from Running/Stopped.
AiDeploymentAnalysis.RetainUntilDeleted separates saved-result lifetime from finite execution expiry.
