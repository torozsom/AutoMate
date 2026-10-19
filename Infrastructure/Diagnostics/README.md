# Deployment diagnostics

Redacted event ingestion, bounded delivery, durable telemetry storage, permanent archives and owner-scoped analytics.

## Contents

| Module                           | Responsibility                                                            |
|----------------------------------|---------------------------------------------------------------------------|
| [Archive](Archive/README.md)     | Permanent checksummed archives, authorized replay, backfill and deletion. |
| [Delivery](Delivery/README.md)   | Bounded dispatch, private gateway transport and backend delivery.         |
| [Metrics](Metrics/README.md)     | Metric normalization, weighted daily aggregation and exploration.         |
| [Policy](Policy/README.md)       | Validated storage options and bounded ownership caches.                   |
| [Redaction](Redaction/README.md) | Credential/text masking and safe event copies.                            |
| [Runtime](Runtime/README.md)     | Expiring process-local viewer interest.                                   |
| [Storage](Storage/README.md)     | Disk spool, Loki/Mimir and legacy PostgreSQL compatibility.               |

## Usage and integration

Delivery owns publisher/dispatcher, gateway transport and backend delivery. Storage contains the disk spool, Loki/Mimir
and legacy read/drain adapters. Archive owns permanent history, authorized reads, backfill and deletion. Metrics owns
sufficient statistics/exploration; Policy owns validated options/ownership caches; Redaction owns masking; Runtime
tracks viewer interest.

## Contracts and lifecycle

Current new payloads use the private disk gateway in both hosting profiles. PostgreSQL payload modes are rejected at
startup/direct legacy writes; existing rows can still be read/drained. Durable acceptance precedes terminal
delivery/checkpoint confirmation. Local live metric snapshots are separate from sampled history.

Runtime collection/approved storage are automatic; viewer presence and legacy preference flags do not authorize AI
egress. Operational backend retention, permanent archive lifetime and analysis-result retention are independent.
Channel/assessment filtering happens before bounded reads in supported archive paths. Preserve availability/gap metadata
rather than imply complete history.

## Related documentation

- [Parent module](../README.md)
- [Contracts](../../Application/Abstractions/Diagnostics/README.md)
- [Platform logging boundary](../Observability/README.md)
- [Storage operations](../../docs/deployment-telemetry.md)
- [Permanent history decision](../../docs/adr/0004-permanent-deployment-history.md)
- [Regression map](../../Test/Infra/Diagnostics/README.md)
