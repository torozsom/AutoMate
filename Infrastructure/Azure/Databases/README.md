# Owned database discovery

Bounded inventory and native metric reads for databases explicitly associated with a deployment.

## Contents

| File                                                           | Types or asset                                       | Responsibility                                                                                      |
|----------------------------------------------------------------|------------------------------------------------------|-----------------------------------------------------------------------------------------------------|
| [AzureManagedDatabaseClient.cs](AzureManagedDatabaseClient.cs) | `AzureManagedDatabaseClient`, `AzureManagedDatabase` | Discovers only AutoMate-tagged databases in a deployment's resource group and reads native metrics. |

## Usage and integration

AzureManagedDatabaseClient enumerates resource-group resources, SQL child databases and explicitly associated database
Container Apps. It supplies native observations to AzureDatabaseDiagnostics; Monitor owns operational log queries.
Supported resource projections include PostgreSQL, MySQL, SQL Database, Cosmos MongoDB and Redis.

## Contracts and lifecycle

Require automate-project-id equal to the project GUID in N format and automate-container-app equal to the recorded app.
SQL child databases need their own tags. Dedicated database apps additionally require automate-database-component
matching an exact recorded suffix and automate-database-provider matching configured composition. Names alone never
establish ownership; web/unrelated apps are excluded. Inventory follows at most ten trusted ARM pages per list and
rejects incomplete results.

Preserve native percent/byte/RU units and minute timestamps. Cosmos uses maximum normalized RU and has no CPU/memory
projection; no values are inferred from percentages or missing workload. Dedicated apps use their own verified ready
revision; shared sidecars remain web-app totals.

For existing deployments, verify recorded providers/tags, regenerate owned infrastructure or apply equivalent verified
settings, and confirm resource/metrics/Logs permissions. Operational export needs actual provider categories enabled:
Cosmos MongoRequests, PostgreSQLLogs and SQL Errors/Timeouts/Deadlocks are defaults; MySQL/Redis remain metrics-only.
Export alone does not enable audit/slow-query collection. Never discover through connection strings.

## Related documentation

- [Parent module](../README.md)
- [Collector](../ContainerApps/Runtime/README.md)
- [Tags/export templates](../../Templating/Templates/README.md)
- [Operational logs](../Monitor/README.md)
- [Inventory tests](../../../Test/Infra/Azure/Monitor/AzureManagedDatabaseClientTests.cs)
