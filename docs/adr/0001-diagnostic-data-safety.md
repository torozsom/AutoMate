# ADR 0001: Diagnostic data safety and AI egress

## Status

Accepted on 2026-10-02.

## Decisions

- Diagnostics are retained only after central redaction, for 30 days.
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
