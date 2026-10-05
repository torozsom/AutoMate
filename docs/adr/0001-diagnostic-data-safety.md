# ADR 0001: Diagnostic data safety and AI egress

## Status

Accepted on 2026-10-02. Delivery buffers in both hosting profiles and aggregate retention are superseded
by [ADR 0002](0002-saas-telemetry-disk-spool.md): deployment telemetry uses disk segments, and daily numeric aggregates
may be retained for 365 days. The 30-day raw-data/redaction/security decisions continue to apply.

## Decisions

- Diagnostics are retained only after central redaction, for 30 days.
- Specialized storage uses Loki for redacted terminal events and Mimir for numeric samples. PostgreSQL retains
  deployment
  metadata and bounded delivery buffers, removed after complete-batch query visibility and capped at 24 hours.
- Query access expires after 30 days. Asynchronous compaction and object reclamation may use a maximum 48-hour physical
  cleanup grace after expiry. Monitor compactor success, deletion backlog and object-store reclamation. This does not
  authorize longer query access. Versions, backups and restores follow the same deadline; longer recoverable copies
  still need a retention exception.
- Background runtime collection requires explicit owner opt-in. Authorized live viewing collects and saves centrally
  redacted runtime diagnostics for 30-day replay while the page is open; viewing leases expire
  after 45 seconds without renewal. Build/deployment history remains automatic. Managed telemetry needs approved
  provider terms/DPA,
  a disclosed approved region and explicit project consent. This ADR approves no managed provider and no LLM egress.
- Backups containing diagnostics must be encrypted and access restricted. Operators must verify that backup lifecycle
  and restore-time expiry cleanup satisfy the 30-day data policy; any longer-lived recoverable copy needs an approved
  retention exception. The repository does not configure the backup provider.
- LLM provider egress is disabled during the diagnostics and streaming milestones. No provider or processing region is
  approved until a later ADR records the provider contract, region, tenant consent, and data-processing terms.
- Automatic failed-deployment analysis is disabled by default. A future design may allow an explicitly authorized
  project owner to opt in; a repeated failed notification must never create duplicate work.
- SaaS tenant diagnostics may not leave AutoMate without explicit tenant consent, approved region, and a provider DPA.
- A durable database queue/outbox remains the required delivery mechanism for future analysis jobs.

## Data policy and threat model

Only redacted normalized diagnostic messages and approved correlation metadata may be persisted, emitted as
OpenTelemetry, replayed to an authorized terminal, or used to construct future analysis context. Raw Docker, GitHub,
Azure, and provider payloads stay in memory until redaction completes.

The controls address credential and PII exfiltration, log-borne prompt injection, unauthorized deployment access,
provider outage, abuse, rate/cost exposure, slow consumers, and exporter or persistence failure. External log text is
untrusted. Secrets in tracked configuration are prohibited; any previously exposed credential must be revoked and
rotated outside this repository.

## Analysis result retention and deletion

Validated analysis results/metadata expire under ResultRetentionDays (1–90 days, default 90) after admission. Query
access and queue eligibility end immediately
at expiry. Startup/hourly bounded cleanup removes expired results and their work items, including while AI is disabled.
Owners may immediately delete terminal or expired results; unexpired queued/running work awaits M6 cancellation support.
No diagnostic context snapshots are persisted. Analysis deletion preserves deployment metadata and existing Loki/Mimir
retention. Remote responses already in flight are discarded after expiry/deletion. Operators must apply the same expiry
and explicit deletion policy to recoverable backups and restore procedures; this repository does not configure backups.

## Queue and SDK export enforcement (M5)

Diagnostic queues admit detached redacted events; the disk spool enforces this itself for direct and HTTP callers.
Checksums are validated against acknowledged bytes before current-policy masking is applied to delivery/replay. Files
are not rewritten during this readback. Analysis work rows contain identifiers/claim metadata, not diagnostic context.
Ownership/consent caches contain short-lived projections. Operational deployment inputs retain required secrets in
process and use the existing protected token-free snapshot converter for durable SaaS configuration; repository cache
DTOs support repository UI behavior. These operational data categories are not approved telemetry/provider context.

Web metric views drop unknown instruments and external HTTP dimensions, retain reviewed finite operational/runtime
labels, and disable exemplars. Resource detection imports exactly four approved fields with bounded masked labels,
excluding arbitrary environment attributes. Custom enrichment or providers bypassing these registrations require their
own review; the supported SDK boundary is not a universal guarantee for arbitrary host/plugin code. Bounded evidence
context and JSON data/fixed-instruction controls are implemented in M5; final provider/tenant/region
egress approval remains open. Exact reference membership does not establish diagnosis correctness.

The M5 context worker keeps selected JSON and a detached read-only evidence catalog in memory, revalidates model
references before result persistence, and rechecks expiry before invocation/publication. Context limits charge encoded
UTF-8 input bytes conservatively as token units; full-request/provider token accounting remains separate. No token-count
network egress or diagnostic snapshot storage is introduced.

## M5 egress enforcement (2026-10-05)

Runtime enforcement now requires explicit operator provider/US-or-EU processing approval, a matching regional endpoint,
an approved owner-account tenant, all current diagnostic categories and fresh project consent before provider calls.
The independent egress switch remains false; no provider, DPA, account, model or processing geography is approved here.
Startup validation rejects requested egress without approvals. Workers check consent before/after context construction;
the real adapter requires deployment scope and checks again without redirects/automatic HTTP retries. Reloadable
configuration denies later calls, but cannot recall in-flight transmission. ResultRetentionDays may shorten new-result
expiry from the default 90 days to at least one day, without extending existing records or changing diagnostic
retention.
The onboarding guide documents provider data-control limitations and operator verification responsibilities.
