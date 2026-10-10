# AI deployment analysis: architecture and onboarding

Implementation status: 2026-10-05. Analysis is available in both hosting profiles behind default-off flags.
Owner accounts are tenants. Provider/account/model/contract/region approval and a live internal pilot remain pending;
USD provider spending defaults to zero. This guide describes implemented behavior, not authorization to enable egress.

## Request and processing path

An authenticated project owner can allow or revoke diagnostic egress for the exact project, request an analysis,
refresh the latest result and cancel queued/running work from the project details panel. The page polls persisted
state every five seconds while applicable. Results display UTC timestamps, trigger, summary, evidence, steps and
available provenance. Text is rendered as text; suggestions are never executed. Deletion is available through the
owner-authorized HTTP API; there is no rendered delete control in the analysis panel.

Manual requests use a stable GUID scoped by owner and deployment. Replays and aliases of active work do not consume a
new quota. Request receipts last ninety days, independently of result expiry; deleting a result cannot recreate it
with the same receipt. Admission/replay still requires current policy and consent. Reads and cancellation/deletion
remain available with the feature disabled.

A PostgreSQL trigger captures new persisted `Failed` transitions in the same transaction as the deployment change.
The dispatcher delivers metadata wakeups to the same admission service used for manual requests. Automatic analysis
requires the global automatic flag plus the ordinary cohort/consent/egress gates. Existing failures are not backfilled.
Completed wakeup markers remain until deployment deletion to prevent repeat admission after result/receipt cleanup.

The separate hosted analysis worker uses one through sixteen processing slots, each with a fresh DI scope and EF
context. It claims atomic renewable leases, builds bounded ephemeral context, rechecks consent/policy, reserves
provider capacity and maximum attempt cost, and invokes the selected adapter. It validates/redacts results before
token-fenced publication. Lease expiry, cancellation or a newer owner prevents stale publication/requeue.
Transient retry scheduling is durable, with bounded exponential backoff/jitter and a fresh context/policy check on
every attempt. Interrupted lease recovery has its own limit. Deployment scheduling/status does not depend on analysis
success, provider availability or AI enablement.

| Application port                    | Responsibility and implementation                                                                                 |
|-------------------------------------|-------------------------------------------------------------------------------------------------------------------|
| `IDeploymentAnalysisService`        | Owner-authorized admission, safe retrieval, cancellation and deletion; `DeploymentAnalysisService`.               |
| `IDeploymentAnalysisQueue`          | Detached claims, renewal and release; `DeploymentAnalysisQueue` respects persisted retry eligibility.             |
| `IDeploymentAnalysisContextBuilder` | In-memory scoped context/evidence; `DeploymentAnalysisContextBuilder`.                                            |
| `IAnalysisEgressAuthorizer`         | Fresh owner, project-consent, tenant and trigger authorization; `AnalysisEgressAuthorizer`.                       |
| `IAnalysisBudgetGuard`              | Atomic shared capacity and conservative attempt-cost reservation; `AnalysisBudgetGuard`.                          |
| `ILlmAnalysisProvider`              | Provider-neutral request/untrusted response; `ConfiguredAnalysisProvider` selects a registered adapter.           |
| `IAnalysisResultValidator`          | Bounded structure, redaction and safe provenance; Application `AnalysisResultValidator`.                          |
| `IDeploymentAnalysisReadiness`      | Finite configuration/credential presence and read-only schema availability; `DeploymentAnalysisReadinessService`. |

Ports live in [Application/Abstractions/Ai](../Application/Abstractions/Ai/README.md), shared policies in
[Application/Ai](../Application/Ai/README.md), and persistence/provider workers in
[Infrastructure/Ai](../Infrastructure/Ai/README.md). Web registers concrete adapters solely in its composition root.
The registered production adapter is OpenAI; this is an implementation choice, not provider approval.

## Storage and data boundaries

New deployment events/logs go to Loki and numeric samples to Mimir through the private disk gateway. PostgreSQL holds
deployment metadata, analysis/result guidance, queue/lease/retry state, receipts, automatic wakeups and budget entries.
Legacy diagnostic history/draining remains readable; there is no new PostgreSQL payload fallback or analysis-context
snapshot. Queue and accounting rows contain metadata, never prompts or diagnostic payloads.

Context selects at most 1,000 scoped event candidates, groups repeated messages and ranks bounded evidence. Numeric
signals summarize known CPU/memory series from at most 300 interval points. Trace signals describe correlation in
selected events; they are not fetched full spans. Coverage records omissions and unavailable sources. Evidence
membership proves a reference was supplied, not that a diagnosis is correct or that the reference will survive
retention.

Central redaction masks supported credential/secret/PII patterns before durable telemetry writes and terminal delivery,
then rechecks context/results at provider and persistence/readback boundaries. It is bounded pattern-based protection:
unknown formats, contextual personal information and arbitrary text cannot be guaranteed anonymous. Anonymize pilot
fixtures before ingestion and review redacted samples before approval. Fixed instructions treat diagnostic JSON as
untrusted data; strict schema, empty tools and exact evidence membership reduce prompt-injection exposure but do not
guarantee correct or safe advice. Supported host/export registrations enforce reviewed fields; arbitrary plugins/custom
providers require their own review.

The OpenAI request sets `store=false`, has no tools and disables redirects/automatic HTTP POST retries. These client
settings do not establish provider-side retention, residency, zero-data-retention eligibility or deletion guarantees.
Operators must verify the actual account/model/region contract and data controls before any pilot. Provider error
bodies and exception text never become saved guidance or operational telemetry.

## Operator configuration

All settings below bind under `AiAnalysis`. Existing defaults require no provider credential or approval.
This safe fragment does not enable analysis or spending:

```json
{
  "AiAnalysis": {
    "Enabled": false,
    "AutomaticAnalysisEnabled": false,
    "ProviderEgressEnabled": false,
    "ApprovedTenantIds": [],
    "DailyTenantCostBudget": 0,
    "MaximumProviderAttemptCost": 0,
    "BudgetCurrency": "USD"
  }
}
```

Before enabling, record provider terms/DPA, account/project, selected model, processing geography and retention/data
controls. Configure `Provider`, `RegionalProcessingApproved`, `ProcessingRegion`, `ApprovedRegions`,
`ApprovedTenantIds`, `AllowedDataCategories`, `Endpoint` and `Model` to match those approvals. The current context
requires `logs`, `metrics` and `traceCorrelation` together. Registered OpenAI routes require the matching
`https://eu.api.openai.com/v1/` or `https://us.api.openai.com/v1/`; global/alternate routes are denied by current
policy.
The default global endpoint/model is not an approved production route/model.

Supply `AiAnalysis:ApiKey` through Web user-secrets for development, `AiAnalysis__ApiKey` in protected deployment
environment configuration or a managed secret store. Never place the key in tracked JSON, sample files, logs or shell
history. Keep the credential scoped to the approved provider account/project; establish rotation and revocation.
Credential presence in readiness does not prove validity, model access or billing readiness.

| Setting                                                                     | Default                  | Bound / behavior                                                                                     |
|-----------------------------------------------------------------------------|--------------------------|------------------------------------------------------------------------------------------------------|
| `TimeoutSeconds` / `MaximumOutputTokens`                                    | 60 / 8,192               | 5–300 seconds / 1–8,192 output tokens, including reasoning.                                          |
| `MaximumContextCharacters` / `MaximumContextBytes` / `MaximumContextTokens` | 24,000 / 48,000 / 12,000 | Each 1–131,072. Token units conservatively count encoded context bytes, not whole billable requests. |
| `MaximumConcurrency`                                                        | 1                        | 1–16 per instance; resizing requires restart.                                                        |
| `LeaseDurationSeconds` / `MaximumRecoveryAttempts`                          | 120 / 3                  | 30–900 seconds / 1–10 acquisitions; synchronize worker clocks.                                       |
| `MaximumProviderRetries`                                                    | 2                        | 0–5 retries after the initial attempt.                                                               |
| `RetryBaseDelaySeconds` / `RetryMaximumDelaySeconds`                        | 10 / 300                 | 1–300 / 5–3,600 seconds; base must not exceed maximum.                                               |
| `DailyProjectLimit` / `DailyTenantLimit`                                    | 5 / 100                  | 0–1,000 / 0–100,000 new analyses per UTC day.                                                        |
| `TenantRequestsPerMinute`                                                   | 10                       | 0–1,000 new admissions in a rolling sixty seconds across the owner's projects.                       |
| `MaximumTenantProviderConcurrency` / `MaximumGlobalProviderConcurrency`     | 2 / 16                   | 0–256 / 0–4,096 live reserved attempts across replicas; tenant ≤ global.                             |
| `DailyTenantCostBudget` / `MaximumProviderAttemptCost`                      | 0 / 0                    | 0–1,000,000 with at most eight decimal places; positive accurate allowances required for execution.  |
| `BudgetCurrency`                                                            | USD                      | Three uppercase ASCII letters; current-day charged currency changes deny execution.                  |
| `ResultRetentionDays`                                                       | 90                       | 1–90; affects new admissions, not existing expiry.                                                   |

Numeric bounds validate even when AI is disabled. Each provider lease reserves the operator's worst-case whole-request
cost without refund, including retries/recovery. Capacity is live-lease based; expired remote requests can continue.
This bounds reserved spend, not invoices if the cost bound is underestimated. Do not derive prices from token metrics
or optional result estimates; configure provider-side spend controls and reconcile actual usage. See
[budget semantics](../Infrastructure/Ai/README.md#shared-tenant-limits-and-reserved-spend-m8).

## Retention, deletion and upgrade

Result access and work eligibility cease immediately at expiry. Startup/hourly retention deletes up to ten batches of
1,000 expired results, then independently bounded receipt and accounting batches. Physical cleanup can lag access
expiry. Owners may cancel unexpired active analyses, then delete terminal results through the authenticated,
antiforgery-protected [HTTP API](../Web/Routes/README.md). Deletion does not erase diagnostic history or refund
allowance.
Receipts retain ninety days and cascade only with project deletion; budget entries retain ninety days independently of
result/project/deployment deletion, cascading only with account deletion. Automatic completion markers last for the
deployment lifetime. Provider copies and recoverable backups require operator/provider deletion procedures; this
repository does not configure them. Detailed diagnostics retain thirty days and daily analytics 365 under ADR 0002.

Stop old Web/worker binaries before upgrading; they do not honor new leases, retries, receipt accounting or shared
budgets. Review/apply the normal ordered EF metadata migration chain before starting current binaries:

| Migration                                              | Added control                                                       |
|--------------------------------------------------------|---------------------------------------------------------------------|
| `20261002062913_AddDeploymentDiagnosticsAndAiAnalysis` | Initial diagnostic/analysis metadata schema.                        |
| `20261004201350_AddAiAnalysisResultProvenance`         | Nullable validated provenance and usage/cost fields.                |
| `20261005091947_AddAnalysisQueueLeases`                | Renewable ownership tokens and interruption counts.                 |
| `20261005094754_AddAnalysisRetryScheduling`            | Durable retry deadline/count.                                       |
| `20261005102239_AddAnalysisAdmissionReceipts`          | Stable request receipts and durable project allowance.              |
| `20261005104851_AddFailedDeploymentAnalysisEvents`     | Atomic failure wakeups and PostgreSQL trigger; no failure backfill. |
| `20261005190054_AddAiTenantBudgets`                    | Independent owner usage and attempt reservation ledger.             |

Use migrations rather than production `EnsureCreated`, which cannot install the failure trigger. Inspect SQL against
the environment's migration history; do not downgrade metadata to re-enable an older worker. The tenant migration
backfills retained consuming receipts through surviving projects, but cannot reconstruct deleted projects or remote
historical charges. Prepared migrations have not been applied to application/production databases by this work.

The [disposable PostgreSQL runner](../deploy/verification/README.md) applies real migrations and tests backfill/shared
locks on its own tmpfs database. This caught and corrected the budget join to `cs_projects.app_id`; no application/
production database is changed. Six real PostgreSQL checks pass locally.

## Observability and rollout evidence

Platform OpenTelemetry setup, safe fields, OTLP export and instruments are documented in
[Application/Diagnostics](../Application/Diagnostics/README.md) and [Web/Observability](../Web/Observability/README.md).
OTLP is optional and operator-configured; deployment Loki/Mimir storage does not itself provide a platform collector
or full trace store. Configure `OpenTelemetry:OtlpEndpoint` to a reviewed HTTP (S) collector URL with no embedded
credentials/query/fragment, plus bounded `ServiceName` and `Environment` labels; exporter authentication belongs in
protected configuration. Export/resource changes require restart. `ExportConsole` is optional for development.
Health probes never call a provider: `/health` is liveness, `/health/ready` checks local policy,
credential presence and bounded metadata/schema reads. Neither certifies trigger installation, worker progress,
provider availability, write permissions or model quality.

Follow the [staged enablement, shutdown and incident guide](ai-analysis-operations.md) and complete the
[internal-pilot acceptance record](ai-analysis-rollout.md) before live rollout. Architecture decisions and remaining
limitations are recorded in [ADR 0003](adr/0003-ai-analysis-execution-and-rollout.md).
Azure OpenAI API-key integration is implemented; follow [Portal and Web user-secrets setup](azure-openai-setup.md).
The [integration record](azure-openai-integration-plan.md) distinguishes verified adapter behavior from pending live
approvals and rollout. Registration alone does not approve Azure egress.
