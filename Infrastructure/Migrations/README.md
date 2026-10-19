# EF schema history

Chronological metadata schema changes grouped into folders by responsibility, plus the latest EF model snapshot.

## Contents

| File                                                                   | Types or asset                   | Responsibility                                                   |
|------------------------------------------------------------------------|----------------------------------|------------------------------------------------------------------|
| [AutoMateDbContextModelSnapshot.cs](AutoMateDbContextModelSnapshot.cs) | `AutoMateDbContextModelSnapshot` | Latest generated EF model used for future migration comparisons. |

| Module                                             | Responsibility                                                      |
|----------------------------------------------------|---------------------------------------------------------------------|
| [AnalysisQueue](AnalysisQueue/README.md)           | AI leases, scheduled retries, receipts and failure wakeups.         |
| [AnalysisResults](AnalysisResults/README.md)       | Diagnostic/analysis baseline, provenance and assessment results.    |
| [Checkpoints](Checkpoints/README.md)               | GitHub/Azure cursors and cloud collector leases.                    |
| [CloudRuns](CloudRuns/README.md)                   | SaaS run/outbox/webhook schema and admission indexes.               |
| [Identity](Identity/README.md)                     | Initial account/project schema and connected-Azure identity fields. |
| [RetentionAndPolicy](RetentionAndPolicy/README.md) | Owner budgets, permanent history and automatic collection policy.   |
| [Telemetry](Telemetry/README.md)                   | Replay ordering, legacy delivery metadata and daily analytics.      |

## Usage and integration

Start with the subsystem folder, then follow timestamped migration IDs. Each migration's .cs file defines Up/Down and
its .Designer.cs companion captures the historical target model. AutoMateDbContextModelSnapshot is EF's latest
comparison baseline. Folder organization does not determine execution order.

## Contracts and lifecycle

Preserve migration IDs, namespace/attributes, SQL, designer pairs and generated markers. A designer is historical
metadata, not the runtime mapping. Review schema changes against Data/AutoMateModelConfiguration and dependent workers;
apply through the established deployment workflow after ordering compatible binaries. This documentation pass
creates/applies no migration and changes no database.

Analysis/telemetry changes include backfills and trigger installation, so an empty-schema comparison alone cannot
establish upgrade correctness. Down operations can remove data; inspect actual SQL and recovery requirements before
using them.

## Related documentation

- [Parent module](../README.md)
- [Runtime mappings](../Data/README.md)
- [Ordered AI upgrades](../../docs/ai-analysis.md)
- [Archive deployment ordering](../../docs/adr/0004-permanent-deployment-history.md)
- [Real migration-chain regressions](../../Test/Infra/Ai/Admission/AnalysisBudgetPostgresTests.cs)
- [Disposable PostgreSQL verification](../../deploy/verification/README.md)
