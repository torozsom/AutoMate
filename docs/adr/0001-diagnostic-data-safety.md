# ADR 0001: Diagnostic data safety and AI egress

## Status

Accepted on 2026-10-02.

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
