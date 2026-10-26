# Persisted domain models

Business state for accounts, projects, deployment history, durable cloud execution, diagnostics and AI analysis.

## Contents

| File or module                       | Responsibility                                                        |
|--------------------------------------|-----------------------------------------------------------------------|
| [BaseEntity.cs](BaseEntity.cs)       | BaseEntity: generated GUID identity and creation/update audit fields. |
| [Ai](Ai/README.md)                   | Results, receipts, budgets, queue leases and failure wakeups.         |
| [Cloud](Cloud/README.md)             | Durable SaaS launches, outbox wakeups, webhooks and cooldowns.        |
| [Deployments](Deployments/README.md) | Launch history, snapshots and archive cleanup.                        |
| [Diagnostics](Diagnostics/README.md) | Collector progress, legacy storage and telemetry metadata.            |
| [Projects](Projects/README.md)       | Application/C# project hierarchy and saved configuration.             |
| [Users](Users/README.md)             | Local/remote accounts and connected-provider credentials.             |

## Usage and integration

Navigate the child guides for models and consumers. [AutoMateDbContext](../../Infrastructure/Data/AutoMateDbContext.cs)
exposes DbSets and save-time audit
handling. [AutoMateModelConfiguration](../../Infrastructure/Data/AutoMateModelConfiguration.cs) owns indexes,
relationships, subtype mappings and protected-value conversions.

## Contracts and lifecycle

BaseEntity assigns a new GUID on construction; the data layer supplies CreatedAt and UpdatedAt during saves. Several
metadata models intentionally use domain-specific keys instead of BaseEntity. Navigation properties describe
relationships; they do not load data, authorize callers or enforce database constraints by themselves. Keep EF Core and
provider behavior outside Domain.

## Related documentation

- [Domain](../README.md)
- [Persistence guide](../../Infrastructure/Data/README.md)
- [Workflow DTOs](../DTO/README.md)
- [Shared enums](../Enums/README.md)
