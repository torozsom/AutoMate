# Azure integration

ARM provisioning, identity, native metrics and ownership-verified Container Apps/database diagnostic collection.

## Contents

| File                                   | Types or asset   | Responsibility                                                                  |
|----------------------------------------|------------------|---------------------------------------------------------------------------------|
| [AzureConstants.cs](AzureConstants.cs) | `AzureConstants` | Shared Azure Resource Manager constants used by the Azure integration services. |

| Module                                   | Responsibility                                                           |
|------------------------------------------|--------------------------------------------------------------------------|
| [ContainerApps](ContainerApps/README.md) | Verified revision recovery, state/metrics, live streams and log cursors. |
| [Databases](Databases/README.md)         | Ownership-tagged managed database inventory and native metrics.          |
| [Identity](Identity/README.md)           | ARM token refresh, managed identities, OIDC claims and RBAC.             |
| [Monitor](Monitor/README.md)             | Scoped log tokens, bounded history queries and native metric parsing.    |
| [Provisioning](Provisioning/README.md)   | Resource-provider registration, federation setup and customer ACR.       |

## Usage and integration

Provisioning prepares resource providers, federation and customer ACR; Identity owns credentials/claims/RBAC.
ContainerApps supervises verified revision/runtime sources, with Runtime collaborators handling databases, availability
and cursor policy. Monitor owns Logs-scoped token exchange and bounded queries; Databases discovers tagged resources and
native metrics.

## Contracts and lifecycle

ARM and Monitor Logs credentials have different scopes. Collect only verified project/resource/revision identities;
never infer a missing historical revision from the newest app revision. Runtime collection is automatic for active
deployments and independent of browser viewers. Observations must reach durable telemetry acceptance before cursors
advance.

Database ownership requires matching project/app tags; dedicated database apps also require exact component/provider
association. Shared sidecar metrics remain app totals. Native units/timestamps survive collection; unsupported
CPU/memory data is unavailable, not zero. Configuration/SDK models and tokens stay inside the adapter boundary.

<a id="aca-live-terminals-2026-10-11"></a>

## Live Container Apps streams

Console/system HTTP streams are supervised separately from historical reads and metric polling. Replica discovery,
short-lived token renewal, trusted endpoint validation and confirmed-tail reconciliation live
in [ContainerApps](ContainerApps/README.md). Historical reads repair gaps without claiming lossless live delivery.

## Related documentation

- [Parent module](../README.md)
- [Azure ports](../../Application/Abstractions/Azure/README.md)
- [Shared DTOs](../../Domain/DTO/Azure/README.md)
- [Cloud launch](../ApplicationServices/Orchestration/CloudPreparation/README.md)
- [Azure regressions](../../Test/Infra/Azure/README.md)
