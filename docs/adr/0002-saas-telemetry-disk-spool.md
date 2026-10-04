# ADR 0002: Deployment disk telemetry and daily analytics

Accepted on 2026-10-04 following the approved telemetry plan; extended to SelfHosted by explicit user request on the
same date.

## Decisions

- Both SelfHosted and SaaS use Loki/Mimir with a separate single-writer disk-spool service. New raw diagnostic payloads
  never enter the
  PostgreSQL outbox and backend outages never select a PostgreSQL payload fallback. Existing PostgreSQL history remains
  readable and legacy outbox rows may drain; Web rejects new database payload writes in either hosting profile.
- PostgreSQL retains control-plane metadata, collector leases/checkpoints and small rebuildable daily summaries.
- Redaction precedes every durable write and customer terminal. Deployment payloads are not mirrored into the
  platform logging pipeline. Existing AI/LLM egress restrictions remain in force.
- Pending disk payloads are bounded to 24 hours and configured per-account/global capacity. Durability receipts follow
  disk flushes. Acknowledged corruption fails closed. This single-volume design does not survive permanent disk loss.
- Detailed logs/metrics retain ADR 0001's 30-day query window and monitored physical cleanup grace. Daily statistics,
  including account/project/deployment identifiers, retain 365 days; they remain tenant-authorized data and must follow
  that same policy in backups/restores. This supersedes ADR 0001's uniform 30-day rule only for these aggregates.
- Tenant identity is server-owned. Project/deployment GUIDs are structured Loki metadata and bounded Mimir labels.
  Customers use AutoMate APIs; Grafana remains an operator tool. No database/bucket/dashboard per deployment is created.
- Runtime collection still requires background opt-in or authorized viewing. Managed-provider consent still gates
  external writes and reads; in either profile missing consent rejects delivery rather than enabling database fallback.
- Provider checkpoints move only after durable acceptance. Collector leases prevent concurrent SaaS replicas collecting
  the same deployment. Provider revision identity is persisted and used to restrict logs and metrics.
- Daily values use sums and sample counts; gaps are not zeros. Log error totals describe observed entries and remain
  approximate under collection gaps and at-least-once retries.

## Consequences

Public operation requires private TLS, encrypted persistent disks/object storage, backup lifecycle enforcement,
capacity alerts and a recovery drill. The checked-in Compose override is a development pilot, not HA production
infrastructure. The ingestion interface permits a future distributed queue without changing customer APIs.
