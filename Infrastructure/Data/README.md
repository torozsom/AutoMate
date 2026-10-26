# Metadata persistence

EF Core context, mappings, protected-value converters and database-level failed-deployment capture.

## Contents

| File                                                                     | Types or asset                    | Responsibility                                                                                              |
|--------------------------------------------------------------------------|-----------------------------------|-------------------------------------------------------------------------------------------------------------|
| [AutoMateDbContext.cs](AutoMateDbContext.cs)                             | `AutoMateDbContext`               | DbSets, save-time audit fields and model-configuration delegation.                                          |
| [AutoMateModelConfiguration.cs](AutoMateModelConfiguration.cs)           | `AutoMateModelConfiguration`      | Owns the existing entity mappings and protected converters separately from persistence-time audit behavior. |
| [FailedDeploymentAnalysisTrigger.cs](FailedDeploymentAnalysisTrigger.cs) | `FailedDeploymentAnalysisTrigger` | Database boundary capture covers tracked saves, direct SQL, bulk cleanup and multiple deployment workers.   |

## Usage and integration

AutoMateDbContext exposes metadata sets and save-time audit handling; AutoMateModelConfiguration separates
relationship/index/protection rules; FailedDeploymentAnalysisTrigger supplies PostgreSQL capture SQL. Web/Telemetry
compose provider configuration and key storage. Migrations contains chronological schema history, not a second model
definition.

## Contracts and lifecycle

SaveChanges populates BaseEntity audit fields. User subtypes use a discriminator; GitHub/Azure tokens and secret-bearing
cloud snapshots use purpose-specific Data Protection conversions. In-memory values remain sensitive. Production
instances must share the intended key ring/certificate configuration for protected data.

Failure capture covers tracked saves, direct SQL and bulk updates through a durable deployment-keyed marker. AI
receipts/budgets have deliberately different deletion relationships; archive cleanup work survives owner/project
deletion. Keep mappings, trigger SQL and migrations consistent. New diagnostics use the private gateway; metadata
context availability is not permission to add payload writes.

## Related documentation

- [Parent module](../README.md)
- [Model hierarchy](../../Domain/Entities/README.md)
- [Schema history](../Migrations/README.md)
- [Failure dispatcher](../Ai/Processing/README.md)
- [Archive cleanup](../Diagnostics/Archive/README.md)
- [Failure-trigger tests](../../Test/Infra/Ai/Processing/FailedDeploymentTriggerPostgresTests.cs)
