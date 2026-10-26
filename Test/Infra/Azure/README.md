# Azure adapter tests

Synthetic provisioning, token/query parsing, owned resource inventory and Container Apps source supervision.

## Contents

| Module                                   | Coverage                                                            |
|------------------------------------------|---------------------------------------------------------------------|
| [ContainerApps](ContainerApps/README.md) | Revision isolation, live streams, durable cursors and availability. |
| [Monitor](Monitor/README.md)             | Native observations, owned database inventory, Logs queries/tokens. |
| [Provisioning](Provisioning/README.md)   | ARM refresh, provider readiness and safe permission failures.       |

## Coverage and navigation

ContainerApps covers revisions/live readers/cursors/source availability. Monitor covers log scope/ingestion order,
native minute metrics and database association. Provisioning covers ARM refresh and provider-registration readiness.
Production adapter guides map each collaborator.

## Fixtures and boundaries

All scenarios here use controlled HTTP/data and owned local metadata; no Azure account, live resource creation or cloud
credential is needed. Assert ownership/revision/token-scope boundaries and finite safe failures. These fixtures do not
establish real tenant permissions, diagnostic export setup or ingestion latency.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra.Azure
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Azure implementation](../../../Infrastructure/Azure/README.md)
- [Ports](../../../Application/Abstractions/Azure/README.md)
- [Cursor state](../../../Domain/Entities/Diagnostics/README.md)
