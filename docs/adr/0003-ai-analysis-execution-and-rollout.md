# ADR 0003: Durable AI execution, owner budgets and staged rollout

## Status and scope

Accepted for the implemented architecture on 2026-10-05. This records M6–M8 controls and the user's explicit
owner-account
tenant boundary and USD/default-disabled spending choices. It supplements [ADR 0001](0001-diagnostic-data-safety.md)
and [ADR 0002](0002-saas-telemetry-disk-spool.md), replacing their deferred analysis queue/cancellation/automatic design
statements. It approves no provider, contract, account/model, processing geography, data egress or live pilot.

## Context

Analysis must tolerate multiple hosts, restarts, slow providers and uncertain remote completion without blocking
deployments, leaking diagnostic payloads into metadata queues or allowing result deletion to reset usage. Configuration
must permit a small internal cohort and independent provider shutdown. Remote calls cannot be made exactly once solely
through local database ownership.

## Decisions

- Use a PostgreSQL metadata queue with atomic renewable token leases, bounded interruption recovery and transactional
  publication/requeue fencing. Each processing slot owns its DI scope/EF context. Persist transient retry deadlines
  and bounded counts; rebuild context and reauthorize every attempt. Do not hold database locks over provider I/O.
- Capture new `Failed` transitions with a migration-owned PostgreSQL trigger and durable wakeup dispatcher. Retain
  completed deployment markers to prevent duplicate automatic admission. Do not backfill existing failures.
- Serialize admission on the owner row then project row. Stable owner/deployment request receipts and active aliases
  preserve idempotency without charging another admission. Per-project receipts retain ninety days; an independent
  owner ledger preserves shared quota/rate usage after project/result deletion.
- Treat the project owner account as tenant in both profiles. Use a shared transaction advisory lock to reserve
  live-lease tenant/global provider capacity and conservative worst-case cost atomically before each attempt. Exact
  integer units use eight decimal places. USD is the default; zero monetary allowances disable spending. Each distinct
  retry/recovery lease reserves again without refund. Operator cost bounds and provider-side controls are mandatory
  for billed-cost assurance; optional token/result-cost metadata is not a price source or accounting ledger.
- Keep feature, automatic-trigger and provider-egress flags off by default. The explicit approved-owner allowlist
  defines rollout scope, combined with fresh consent for the exact project. The automatic toggle is global additional
  opt-in; there is no separate per-project automatic preference. Consent alone cannot override operator approvals.
- Initially register only the OpenAI adapter behind a provider-neutral catalog/port with exact approved regional route
  checks. Unknown routes/providers fail closed before adapter resolution. Adding a provider requires registration,
  approval review, equivalent consent/redaction/budget/cancellation controls and credential-free tests.
- Subscribe to reloadable AI policy during each OpenAI request, cancel active local HTTP I/O on any snapshot change and
  classify shutdown as unavailable rather than retryable. Dispose subscriptions at completion and reject late results
  after reload. Environment/command-line changes require restart; each replica must observe the effective shutdown.
- Keep context/evidence ephemeral. Validate response structure, redaction, bounded provenance and exact selected
  evidence membership at adapter/worker boundaries. Persist only validated guidance and metadata. Render advice as
  text and require human review; never execute suggestions.
- Retain results for configured 1–90 days, default ninety, and receipts/accounting for ninety days independently.
  Expiry excludes reads/work immediately; startup/hourly cleanup is bounded. Active owners may cancel then delete;
  deletion neither refunds usage nor changes diagnostic retention. Completed automatic markers live with deployments.
- Expose provider-free finite readiness and safe workflow/security telemetry. Keep deployment work independent of
  analysis success and maintain ADR 0002's required disk gateway/Loki/Mimir boundary for new diagnostic payloads.

## Consequences and limitations

Lease fencing protects local state, not exactly-once remote execution/billing. A crash, timeout, expired lease or local
cancellation can leave a remote request running; recovery may incur another charge. Capacity is based on live reserved
leases, not all remote requests. Underestimated attempt costs cannot guarantee invoice ceilings. Shutdown starts after
local configuration propagation and cannot recall transmitted data; a final-check/transmission race remains.

Pattern-based redaction cannot prove complete anonymization. JSON/data instructions, strict output and evidence
membership reduce attack surface but do not establish diagnosis correctness. Selected windows omit history; trace
correlation summaries are not complete traces. Evidence can expire independently of retained guidance. Client
`store=false` does not establish contractual provider retention/residency. Backups, provider deletion and encrypted
production storage require operator procedures.

Mixed-version workers are unsafe; migrate after stopping old binaries. Receipt/budget backfills cannot reconstruct
deleted history or prior remote charges. SQLite regressions verify concurrency/state behavior but do not certify
production throughput or HA. Real migration, owner/advisory-lock and exact failure-trigger checks now pass on the
owned local PostgreSQL runner; production permissions/HA and external-stack checks remain environment-specific.
A configured readiness probe proves neither provider availability nor write/
trigger permissions, lease progress or model quality.

## Rollout and evidence

Use [onboarding](../ai-analysis.md), [operations/shutdown](../ai-analysis-operations.md) and the
[pilot acceptance record](../ai-analysis-rollout.md). Begin with anonymized samples and a small explicit internal
manual cohort; enable automatic analysis and expand only after recorded acceptance. Source-level documentation and
credential-free checks are complete; live provider approval, migrations, pilot execution and acceptance remain pending.

## Azure API-key extension (2026-10-06)

Both profiles now register `azure-openai` alongside the unchanged direct OpenAI route. Infrastructure owns the nested
Azure resource/API-key configuration and shares the tested bounded Responses protocol between adapters. No managed
identity or implicit authentication/provider fallback is introduced. Nested key/resource changes invalidate the
enclosing
AI options and cancel active local I/O. Provider-free readiness checks only local configuration. This adds no metadata
migration or provider/data-processing approval. See [setup](../azure-openai-setup.md) and
[implementation/acceptance record](../azure-openai-integration-plan.md).
