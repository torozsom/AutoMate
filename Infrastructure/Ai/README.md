# Deployment analysis infrastructure

`OpenAiAnalysisProvider` requests a strict structured result with storage disabled and no model tools. It reads only a
completed assistant output-text result, allowing leading reasoning items, and rejects refusal/incomplete/malformed,
ambiguous or schema-divergent output. Successful bodies are capped at 128 KiB while reading, including responses with no
Content-Length. Only bounded 429 error codes are inspected in memory for classification; error messages/bodies are
never logged or persisted. A linked per-call deadline governs headers and body;
repeated calls do not mutate a shared HttpClient's base address or timeout.

AiAnalysis:MaximumOutputTokens supplies Responses max_output_tokens, including reasoning allowance (1–8,192, default
8,192). TimeoutSeconds is validated at 5–300 seconds (default 60) and used directly without silent clamping. Common
egress policy rejects invalid timeout/output configuration before transport, including custom option monitors. API keys
remain protected configuration; no key is required for default-off startup. Output/body/result caps remain independent;
a low allowance can produce incomplete output, which is safely rejected rather than published.

The adapter records the actual response `model` separately from the configured requested model, AutoMate's fixed prompt
version and the validated result schema version. Optional input/output counts come from `usage`. A separate model
revision
and cost estimate are left null because the Responses envelope does not provide those fields. The recorded model ID may
still be an alias; it is not claimed to be an immutable snapshot. Response parsing follows the
[official Responses reference](https://developers.openai.com/api/reference/typescript/resources/responses/methods/create)
and [Structured Outputs guidance](https://developers.openai.com/api/docs/guides/structured-outputs).

`DeploymentAnalysisWorker` validates/redacts responses again before publishing result fields through lease-fenced
writes.
Malformed results become Failed/invalid_response; permanent provider failures become Failed/provider_failure; transient
failures queue a bounded retry or finish as Failed/retry_exhausted. Explicit unavailable
configuration becomes Skipped/unavailable; empty context becomes Skipped/unsupported_data. Exception messages and raw
response bodies never become
saved summaries. Caller cancellation propagates without completing the work item. Deployment status is not modified.
Atomic leases, durable retries and configurable per-instance concurrency are implemented below; tenant/cost
controls remain M8 work. Egress/consent/region gates are described below.

`DeploymentAnalysisService` preserves owner authorization and consent while hardening admission/quota races below.
Readback
validates
and redacts saved results, including legacy rows, without changing their stored schema/provenance. Corrupt results
return
a safe failed view. Skipped summaries use fixed safe wording; unknown failure codes and metadata are not exposed.

`AddAiAnalysisResultProvenance` adds eight nullable columns to ai_deployment_analyses only. Existing rows remain valid
without fabricated usage/revisions. Apply this migration through the existing operator deployment workflow before
running
the updated Web host. Analysis metadata and validated result guidance remain in PostgreSQL; no new log/metric writer,
context snapshot buffer or PostgreSQL telemetry fallback is introduced. Loki/Mimir and the disk gateway keep their
current responsibilities. The migration was scaffolded and its SQL/model verified; it has not been applied to a live DB.

Tests in `Infrastructure.Tests/Ai` use fake HTTP envelopes and SQLite metadata. They cover shape/size limits, all text
fields, actual model/usage, invalid/partial/refusal responses, unannounced body sizes, safe failures, cancellation,
redacted persistence, legacy readback and ownership. Hosting-profile tests resolve the new ports with AI disabled.
No tests make a real LLM request. Supported M5 boundaries are verified; remaining M6 cancellation/admission hardening
and M8 production controls remain open.

## Retention and deletion

Analysis metadata expires under ResultRetentionDays (1–90 days, default 90) after request admission.
`DeploymentAnalysisRetentionService` runs at startup and
hourly
in both hosting profiles even with AI disabled. Each pass deletes up to ten batches of 1,000 expired analysis rows;
existing foreign-key cascades remove their work items. A backlog larger than 10,000 continues on the next hourly pass.
Readback, request deduplication and queue claims exclude expired rows immediately, without waiting for physical cleanup.
The worker rechecks expiry before provider invocation and conditionally publishes results/work completion in a database
transaction; an already-running provider response is discarded after expiry/deletion. Cleanup does not cancel remote
requests, alter deployments or delete Loki/Mimir data. Existing expiry indexes/cascades suffice; no new migration is
added.

`IDeploymentAnalysisService.DeleteAsync` performs owner/deployment/state checks inside the delete statement. Missing and
foreign-owned IDs return the same NotFound result. Owners can remove terminal or expired analyses, including their queue
rows; unexpired Queued/Running work returns InProgress. Durable project quota usage now survives deleted results;
tenant/cost
accounting remains M8 work. No durable diagnostic context snapshots exist: context is
constructed in memory from the existing diagnostic port, and this feature introduces no snapshot storage.

The authenticated, antiforgery-protected HTTP route is documented in [Routes](../../Web/Routes/README.md). Metadata
persistence tests cover bounded/all-state expiry, ownership, active-work conflicts, cascades, cancellation and late
provider completion with and without concurrent cleanup. HTTP tests exercise real middleware over loopback without
external services or LLM requests.

## Operational logging

The hardened worker emits fixed-name claim, processing, context, provider, heartbeat, release, publication and retry
activities. Outcomes reflect committed writes: stale publication is discarded, durable requeue is retry_scheduled,
and skip/cancellation differ from provider failure. Queue acquisition measures eligibility wait without identifier
dimensions; each processing scope balances the active counter. Provider-reported usage is recorded only after schema,
redaction and evidence validation, before publication, so a billed but stale result remains measurable. Export policy
and instrument definitions are documented in Application/Diagnostics/README.md. No context or result text is exported.

The analysis worker adds deployment/analysis GUID scopes and fixed processing/provider outcome events. Cancellation
propagates and is audited; validated success, rejected results, unavailable providers, provider failure and discarded
late results never attach context, result text or exception bodies. Retention emits a completion event only when rows
were removed. This adds observability without changing AI defaults, queue behavior, deployment state or payload storage.
Automatic admission and per-instance concurrency are implemented below; AI tracing conventions remain M6 work.
Persistence tests inspect actual worker
log attributes/scopes for every terminal path and confirm injected secrets are absent.

The provider now injects the shared text redactor and rechecks context immediately before constructing the outbound
HTTP body. Oversized context is rejected before HTTP transport rather than silently cutting its evidence. The
configured character bound remains in use; byte/token prioritization and semantic evidence grounding remain M5 work.
Synthetic request-body tests verify that context credentials are masked while API authentication and safe diagnostics
are preserved. AI remains disabled by default; this change does not authorize or enable provider egress.

The M5 queue/cache audit confirms analysis work rows contain IDs/claim metadata only; diagnostic context is loaded
inside the worker immediately before provider invocation and is not cached or persisted in queue entries. See
../Diagnostics/README.md for the distinction between diagnostic payloads and required operational deployment inputs.

## Context acquisition and grounded results

DeploymentAnalysisContextBuilder reads deployment/owner/project metadata, then at most 1,000 scoped terminal events from
the existing diagnostic port and at most 300 Mimir interval points. Foreign-project/deployment rows are excluded. The
metric window is at most one hour ending at the latest observed log time (or now), bounded by deployment creation and
30-day retention. Managed storage consent is checked before Mimir transmission. Optional metric failures become a
fixed availability flag; caller cancellation propagates. Raw provider errors and container names never enter summaries.

Application/Ai selects a bounded JSON document with typed coverage, records, metricSignals and traceSignals. The worker
uses this port instead of the compatibility BuildContextAsync line-joining API. Legacy and Loki terminal projections
now retain timestamp/severity/sequence and canonical trace/span identity without changing stored rows, cursors or
presentation. Context and candidate/reference catalogs remain in memory; no schema migration or context cache is added.

OpenAI prompt version deployment-diagnostics-v2 treats JSON-encoded diagnostics as data under fixed instructions, allows
only references present in the context, and states omission/trace/aggregate limitations. The adapter rechecks character,
encoded-byte and conservative context token-unit limits before HTTP, masks text again, and still requests strict JSON
schema, store=false and an empty tool list. Adapter membership applies to grounded requests; the worker always rechecks
it after structural/redaction validation. Invalid evidence becomes Failed/invalid_response without saving guidance.
This does not establish that a model's diagnosis is correct. AI remains default-off; tenant/region egress approval and
automatic admission and per-instance concurrency are implemented below. No live LLM request is made for token counting
or tests.

## Provider egress policy

`AnalysisEgressAuthorizer` implements `IAnalysisEgressAuthorizer` using a fresh untracked deployment metadata query.
Owner-account GUIDs are the current tenant boundary; a request cannot supply a replacement tenant identifier. Both
SelfHosted and SaaS require operator approval and current project `AiDiagnosticEgressConsented`. Manual admission gates
before creating queue metadata. Workers gate before reading context and again after construction; the real adapter
requires DeploymentId/Trigger and gates again before transport. Revocation skips existing work with the fixed
`unavailable` code; safe retrieval/deletion and deployment behavior continue independently. Missing deployment metadata
and disabled automatic triggers also deny. Plain standalone requests without a deployment scope can no longer invoke
the real adapter, although their additive request constructors remain source-compatible.

Committed `AiAnalysis` configuration contains false switches and empty approvals. To onboard a provider, first record
the M0 provider/contract/account/project/model and processing-region decision; this implementation makes no such
approval.
Then configure all of these through operator configuration:

- `Enabled` and `ProviderEgressEnabled`: both true only after approval. Turning ProviderEgressEnabled off denies new
  calls through reloadable IOptionsMonitor; providers that do not support configuration reload require a restart.
- `Provider`: exactly `openai`; `RegionalProcessingApproved`: true only after the operator verifies contract and
  account/project/model eligibility. This is an attestation, not an account-policy verification API.
- `ProcessingRegion`: `eu` or `us`, explicitly present in `ApprovedRegions`; `Endpoint`: exactly the matching
  `https://eu.api.openai.com/v1/` or `https://us.api.openai.com/v1/`. The global endpoint, alternate hosts, proxies,
  credentials, explicit ports, queries, fragments and alternate paths are denied. TLS validation remains standard.
- `ApprovedTenantIds`: nonempty approved owner-account GUIDs, never Guid.Empty. Project consent is a separate required
  metadata check. AutomaticAnalysisEnabled is an additional gate for DeploymentFailed triggers and stays false.
- `AllowedDataCategories`: exactly `logs`, `metrics`, `traceCorrelation`. The current builder combines these categories;
  incomplete approvals deny the entire call rather than silently transmitting an unapproved category. Granular
  category selection requires a later context-contract change. Operational snapshots/cache values are excluded.
- Context character/encoded-byte/conservative-token bounds: each 1–131,072, using the documented existing budgets.
  `ResultRetentionDays`: 1–90, default 90, applied only to new result/work metadata. Existing expiry and Loki/Mimir
  diagnostic retention are unchanged; no diagnostic snapshot is persisted.
- Supply `AiAnalysis:ApiKey` only via a protected credential source. No real credentials belong in tracked settings.

Requested egress with invalid approvals fails startup option validation. Runtime policy remains fail closed even for
synthetic/custom options sources. If the policy snapshot changes during final authorization, the adapter sends nothing.
The typed client disables redirects and automatic resilience retries: a redirect is an unsuccessful response, and
durable retries return through the policy gate. No automatic POST retry can reuse earlier consent.

The [OpenAI data-controls documentation](https://developers.openai.com/api/docs/guides/your-data) describes US/EU
regional
processing, account/model eligibility and exceptions. Choosing a hostname and store=false does not establish zero data
retention, approve a DPA, or prove every transport/system-data path stays within a geography. Network/proxy policy and
provider account controls remain operator responsibilities. Revocation cannot recall bytes already sent; there is a
small race between the final metadata check and HTTP transmission, and in-flight requests are not remotely recalled.
The implementation does not hold database locks across external network calls.

Synthetic policy, SQLite consent/tenant/admission and fake-HTTP tests cover default denial, region/route/category
checks,
revocation before/during context construction, configuration reload, missing scope and bounded retention. They never
contact OpenAI.

## Durable ownership and interruption recovery (M6)

DeploymentAnalysisQueue selects eligible metadata then acquires ownership with a conditional database update.
Each acquisition has a fresh GUID token; unfinished Queued/Running work recovers after expiry. Terminal and expired
analyses are excluded. Legacy ClaimedAt-only rows recover through the new nullable lease columns.

LeaseDurationSeconds defaults to 120 (30–900). Renewal runs in independent scopes at most every ten seconds;
uncertain ownership cancels local processing. Shutdown attempts a separate five-second bounded release. Running
transitions and transactional result/work completion require the current unexpired token, so a stale response cannot
overwrite a newer completion. Application clocks across instances must be synchronized. No database lock spans an
external provider request. Each instance defaults to one processing slot; MaximumConcurrency configures additional
slots.

MaximumRecoveryAttempts defaults to three (1–10). A subsequent acquisition finalizes repeated interruption as
Failed/recovery_exhausted without loading context or invoking a provider. This bounds crash recovery, rather than
retrying transient provider responses. Durable provider backoff is implemented below; owner cancellation is implemented
below.
A crash after sending a request can cause another remote invocation; local fencing does not guarantee exactly-once
provider billing or execution.

Apply the reviewed AddAnalysisQueueLeases metadata migration before running this worker. Stop old workers first:
older binaries do not honor ownership tokens, so mixed-version processing is unsafe. The migration has been scaffolded,
not applied by this implementation. Diagnostic payloads continue through Loki/Mimir; queue rows hold metadata only.

## Durable transient provider retries (M6)

AnalysisRetryPolicy defaults to two retries after the first call, a ten-second initial exponential wait, and a
300-second maximum. MaximumProviderRetries accepts 0–5; RetryBaseDelaySeconds accepts 1–300 and must not exceed
RetryMaximumDelaySeconds (5–3,600). Runtime custom options are bounded too. Positive jitter is added within the cap.
Valid Retry-After delta/date hints are minimums: a hint above the configured maximum stops retries rather than sending
early. Expiry also prevents scheduling an attempt outside result retention. Lowering the retry budget cancels further
retry eligibility at processing time with a safe retry_exhausted outcome.

The adapter signals only explicit 408/500/502/503/504, connection/stream failures, provider timeouts, and recognized
429 rate_limit_exceeded/slow_down codes. A 429 envelope is read only in memory, bounded to 8 KiB/depth eight, and
discarded;
quota/billing/unknown/malformed errors remain terminal. Permanent HTTP failures and invalid responses are terminal.
Caller cancellation propagates. No raw provider message/body or inner exception crosses the transient boundary.
Transport retries/redirects remain disabled; one adapter call sends at most one request.

The worker atomically transitions Running to Queued, increments ProviderRetryCount, persists NextAttemptAt, resets the
interruption acquisition counter and releases its lease. The transaction requires current unexpired ownership; stale
failures cannot requeue a newer completion. Only metadata is persisted. New connections/hosts respect the deadline.
Every attempt rebuilds context and rechecks owner/project/operator policy; no previous prompt or consent is reused.
AuditOutcome.RetryScheduled records the safe fixed outcome. Retry/crash counts are separate; remote calls after a
transport failure or crash may still have executed and may incur duplicate charges. This is not exactly-once billing.

Stop older workers and apply AddAnalysisRetryScheduling before starting these binaries; old versions do not honor
NextAttemptAt. The migration was prepared and SQL-reviewed, not applied. Loki/Mimir storage, existing deployment state
and AI default-off behavior remain unchanged. Tenant quotas/cost accounting and
AI workflow activities/meters are implemented through Application/Diagnostics/AnalysisTelemetry; M8 tenant/cost controls
remain open.

Retry classification/backoff follows
the [official OpenAI guidance](https://developers.openai.com/api/docs/guides/rate-limits),
with a conservative allowlist for temporary errors.

## Owner-requested cancellation (M6)

DeploymentAnalysisService.CancelAsync applies owner/deployment/analysis/expiry predicates to the cancellation write.
Queued/Running analyses, including delayed retries, become Cancelled; the same transaction completes unfinished work,
clears its lease and future eligibility, and removes any partial guidance/failure code. Repeated cancellation returns
success; finished results are preserved. Missing/foreign/expired IDs share NotFound. Operator enablement and provider
consent do not block cancellation. A completed owner action emits CanceledByOwner at Information with GUID correlation.

Workers reject late results and retry scheduling because the state/token predicates no longer match. The next lease
heartbeat cancels cooperative local context/provider processing; the normal interval is at most ten seconds and an
uncertain renewal is additionally bounded by its ten-second database deadline. Ownership loss is a normal discarded
attempt, while host cancellation still propagates. An uncooperative adapter response is fenced at publication. Requests
already sent, or racing the ownership check and transport, may complete remotely and incur charges; local cancellation
cannot recall provider-side execution.
Deployment state, log/metric history and Loki/Mimir retention are untouched.

Cancelled is appended as persisted status value 5, preserving existing 0–4 values; no new columns/migration are
required.
Canceled readback supplies fixed wording without partial guidance/provenance. Owners can subsequently delete the result
through the existing deletion port. HTTP cancellation is documented in Web/Routes/README.md; rendered controls remain
M7.
SQLite independent-connection, stale-response, context/heartbeat, rollback and loopback HTTP regressions cover this
slice.

## Manual admission and request receipts (M6)

RequestManualAsync retains its existing overload, coalescing active work. The new overload accepts a nonempty stable
GUID requestId; clients should reuse it after an uncertain response. Its canonical key includes owner and deployment
GUIDs, so another deployment/owner cannot reuse it to reveal or affect a request. The same key returns its prior live
analysis, including completed/canceled results, without new work or quota charge. A deleted/expired result produces
fixed unavailable guidance rather than creating work again. Admission/replay still require current enablement,
ownership, consent and operator policy; GetLatestAsync remains the read path when AI is disabled.

A short transaction obtains a project row lock using Name = Name; the name/configuration and audit timestamps are
unchanged. Production PostgreSQL uses Read Committed so queries after waiting see committed receipts. Fresh ownership/
consent reads occur after the guard. No diagnostic context, provider call or remote network work occurs under the lock.
All manual admission paths must use this guard; older binaries do not participate and cannot run during rollout.
Different projects have independent PostgreSQL guards; SQLite regression fixtures serialize writes more broadly.

AiAnalysisRequest stores only GUID identities, a bounded canonical key, UTC admission day, quota-consumption flag and
expiry/audit timestamps. New analysis + work + receipt commit together. Active aliases have their own stable receipts
without consuming another allowance. Failed writes roll back and detach only that admission's entities, allowing a
reused scoped context to retry safely. DailyProjectLimit defaults to five, accepts 0–1,000, and counts receipts marked
ConsumesQuota; zero denies new analyses. Existing active work/replays are resolved before the quota check. A separate
1,000 receipt/project/day cap bounds aliases; existing keys remain replayable at the cap.

Receipts survive analysis result and deployment deletion; only project deletion cascades them. They expire after ninety
days, independently of shorter result retention. Retention cleans receipts in at most ten batches of 1,000 per hourly
cycle. Result deletion never refunds a day's usage; a new UTC day has its own allowance. Idempotency is a ninety-day
window, not an unlimited historical guarantee. No diagnostic snapshot or log/metric payload is stored in receipts.

AddAnalysisAdmissionReceipts creates metadata/indexes and backfills surviving analyses from the preceding ninety days,
using explicit UTC days and GUID-only legacy keys. Previously deleted results cannot be reconstructed. Stop all older
Web/worker instances, apply the reviewed migration, then start admission-aware binaries through the operator rollout
workflow. This implementation prepares/reviews SQL but does not apply it. Legacy pre-existing duplicate active results
are not automatically rewritten; manual admission coalesces the latest one. Automatic admission and per-instance
concurrency are implemented below; provider selection is implemented below; tenant/cost budgets remain M8 work.

## Automatic failed-deployment admission

FailedDeploymentAnalysisDispatcher polls up to 100 metadata wakeups every five seconds with a fresh admission scope
per deployment. PostgreSQL capture, installed by AddFailedDeploymentAnalysisEvents, inserts one wakeup atomically with
a real status transition to Failed (including a newly inserted Failed deployment). Repeated Failed writes, UI events,
and subsequent failures of the same deployment cannot reset the marker. Existing Failed rows are not backfilled.

DeploymentAnalysisService.RequestAutomaticAsync uses the manual project-row admission guard and shared UTC quota
receipts, fresh owner/configuration/consent/egress policy, and a current Failed status. Automatic analysis remains
operator opt-in and project-consent gated. It coalesces active manual work. New analysis/work/receipt and wakeup
completion commit together; an insert failure leaves the deployment Failed and wakeup pending. Competing dispatchers
use fresh untracked marker reads plus a completion compare-and-set, preventing stale scoped state from re-admitting.
Completed markers survive analysis/result/receipt cleanup until deployment deletion. They contain only GUIDs/timestamps.

Global-disable/consent denial completes the wakeup without a result or provider call; enabled consented policy/quota
denial creates a terminal Skipped result as described below. Enabling AI or granting consent later
does not replay completed historical failures. A pending wakeup is evaluated against current settings when dispatched;
a crash before denial completion may cause re-evaluation. Transient admission/database errors retain pending wakeups;
individual errors do not block the rest of a batch. There is no context/log/metric outbox or PostgreSQL payload
fallback.

## Configurable worker concurrency

AiAnalysis:MaximumConcurrency defaults to 1 and accepts 1–16 processing slots per application instance. Web validates
this range at startup even with AI disabled; the worker also rejects invalid values rather than silently clamping them.
Set AiAnalysis__MaximumConcurrency in the environment, or MaximumConcurrency in the AiAnalysis configuration section,
and restart the application. The worker snapshots IOptions at startup; hot configuration reload does not resize slots.
The existing IOptionsMonitor-based consent, operator policy, recovery and retry checks remain fresh for each operation.

Each slot creates a fresh async scope, claims one eligible lease, processes it through RunLeasedAsync and awaits
heartbeat/cleanup and scope disposal before another claim. There is no prefetch or unbounded task creation. Context,
provider and DbContext instances are scoped independently. A failed claim/processing loop backs off for two seconds;
a provider failure becomes the existing safe result or retry outcome and frees its slot. Delayed retries wait in the
queue rather than occupying a processing slot. Shutdown cancels every slot and awaits all coordinator cleanup through
the hosted-service stop contract; lease expiry remains recovery if bounded release cannot reach the database.

This is a per-instance processing bound, not a cluster-wide tenant/provider budget. Two instances configured with three
slots can run six distinct analyses; existing conditional database claims and result fences protect ownership. M8
cluster/tenant cost and concurrency controls remain open. No new migration, provider call or payload storage is added.

## Provider registry and consistent skipped results

ConfiguredAnalysisProvider selects through AnalysisProviderCatalog using current IOptionsMonitor<AiAnalysisOptions>.
Provider remains the existing canonical string configuration key, so registered adapters do not require an Application
enum or workflow/UI change. Each immutable AnalysisProviderRegistration supplies its identity, implementation type and
an exact route/region approval predicate. Shared category, tenant, region and context constraints come from
AnalysisEgressPolicy.IsCommonConfigured. Web options validation and AnalysisEgressAuthorizer use the same catalog.
The legacy IsConfigured helper retains the initial exact OpenAI policy for the OpenAI adapter and standalone callers.

OpenAI is registered as a concrete typed HttpClient, with redirects disabled, behind the scoped router. Missing/unknown/
disabled/unapproved selections never resolve an adapter. Reload during resolution fails closed; a response claiming a
different provider identity is rejected. To add a provider, implement ILlmAnalysisProvider in Infrastructure, register
its scoped/typed client and an AnalysisProviderRegistration with its exact approved-route predicate. Each adapter must
retain bounded transport, credential handling, fresh metadata authorization, context limits/redaction and result policy;
registration is a code boundary, not a configuration-driven permission to send to arbitrary endpoints. No alternative
live provider is introduced or approved in this slice.

Enabled, owner-authorized, consented requests denied by egress/provider policy or the UTC project allowance now persist
a terminal Skipped result. Admission records no work item, stores fixed unavailable provider/model placeholders, uses
an authored unavailable/quota_exceeded reason and consumes no project analysis allowance. Result plus non-consuming
receipt and automatic wakeup completion commit atomically. Stable request IDs replay the same skip; deletion/retention
cannot turn that key into another request. These records share the existing 1,000-receipt project/day cap; at the cap,
no additional metadata is created. Owner/consent denials and globally disabled AI still create no analysis. Automatic
analysis disabled or no longer Failed also creates no analysis, completing its wakeup as before.

Requests accepted before credentials become unavailable or consent is revoked finish through the existing fenced
worker as Skipped/unavailable; previously admitted work keeps its original quota charge. Empty bounded diagnostic
context becomes Skipped/unsupported_data without a provider call. Skipped readback ignores saved summary/provenance/
steps/evidence and emits only fixed guidance and known codes, including safe fallback for legacy/unknown codes.
Accepted=false may contain the owner-visible terminal skipped view; no work is queued. Deployment state, disabled AI
and Loki/Mimir payload storage are unchanged. This slice introduces no migration or live provider request.
