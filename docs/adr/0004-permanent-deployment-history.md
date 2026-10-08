# ADR 0004: Permanent owner-controlled deployment history

Accepted on 2026-10-08 following the owner's explicit implementation plan.

## Decision

Retain deployment metadata, centrally redacted archive logs/metrics, project analytics aggregates and saved AI results
until owner deletion. This supersedes ADRs 0001–0003 only for these saved records. Loki/Mimir operational retention
stays 30 days; spool buffering stays finite. AI work execution expiry, admission receipts and spending records retain
their existing independent cleanup and accounting semantics. Manual analysis supports every deployment status; automatic
analysis remains failure-only. No new deployment deletion UI is introduced.

Capture only whitelisted launch facts before provider work: project identity, source repository, branch, environment,
runtime, port, provider/resource names. Never snapshot credentials or environment-variable values. Discovered
commit/image/container/revision/run facts belong to that deployment. Historical configuration is never reconstructed
from current settings. Legacy unknown outcomes remain explicit; Running/Failed metadata supports positive outcome
backfill.

The private single-writer Telemetry process stores deployment-partitioned SHA-256 checksummed segments beneath
DiskSpool:Directory/archive. It flushes archive persistence before returning ingestion receipts, then maintains the
existing spool/delivery path to Loki/Mimir. Duplicate identities return the original receipt. Queries bound page memory
and chart intervals independently of age, reapply current redaction, and recheck owner/consent. Corrupt segments fail
explicitly. A deleted-project tombstone blocks late collector writes.

The archive backfill worker imports up to 20 deployments per discovery pass, at most 500 legacy/log events per source
per deployment and one day of bounded metric intervals per pass. Checksummed progress is atomically flushed after
archive writes. Imported metric means/extrema are interval statistics, not raw sample-weighted values; raw archived
samples take precedence in overlapping buckets. Already-expired records cannot be recovered. Backend failures retry
without advancing that source checkpoint. Existing legacy retention workers continue to run, so enable backfill
promptly.

Project deletion (including account cascades) transactionally queues archive cleanup without foreign keys to deleted
owners. Authorization denies deleted resources immediately. The retrying cleanup worker deletes payloads and then
removes the outbox record; the tombstone remains. Operator recovery may retry cleanup safely.

## Rollout

1. Stop old Web/AI workers and the old Telemetry writer. Mixed versions can delete retained results or acknowledge
   events without archival.
2. Back up PostgreSQL and the encrypted persistent telemetry volume. Apply migration
   20261008202231_PreserveDeploymentHistory with the configured Web startup project.
3. Deploy/start archive-capable Telemetry first against the same persistent DiskSpool:Directory. Keep exactly one
   writer; verify authenticated status, archive ingestion/read, backfill progress and deletion backlog.
4. Deploy updated Web and workers. Verify current/historical pages, consent, and provider-free archive reads before
   initiating paid analysis.
5. Monitor volume free space, inode/file count, backup completion, query duration, backend delivery and cleanup
   failures. This implementation uses one segment per event and bounded-memory partition scans; query I/O grows with
   partition size. Large installations need measured capacity planning and a future indexed/compacted archive.
6. Exercise restart and backup restore with checksum validation; restore deletion outbox/tombstones consistently before
   reopening customer access.

Permanent retention makes storage grow with accepted diagnostics. Provision the volume for expected message rate and
metric sampling across all retained projects, plus backups. Encrypt backups, restrict access and define a deletion-aware
lifecycle: restoring a backup must replay committed deletion cleanup before exposing it. Backups must not silently
reintroduce deleted projects. There is no automatic archive age expiry, remote archive replication or guarantee against
permanent disk loss.

The configured AI reservation remains $0.07 per provider attempt, with a $50 USD daily account ceiling and retries
disabled. Keeping more results does not refund reservations, reset quotas or authorize additional egress.
Empty/unavailable historical evidence skips provider work with existing finite guidance.
