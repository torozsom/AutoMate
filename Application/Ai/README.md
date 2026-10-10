# AI result policy

`Enabled`, `AutomaticAnalysisEnabled` and `ProviderEgressEnabled` are independent default-off flags. `ApprovedTenantIds`
is the explicit staged owner-account cohort, with current per-project consent still required. The
[operator rollout path](../../docs/ai-analysis-operations.md#staged-feature-enablement-and-shutdown) defines promotion
and rollback. OpenAI and Azure OpenAI cancel local in-flight HTTP work on AI option reload; propagation and remote
recall limitations
remain. Disabling analysis does not disable deployments or existing authorized result management.

`AnalysisBudgetPolicy` validates tenant admission/rate limits, shared provider concurrency and exact eight-decimal
monetary settings independently of AI enablement. BudgetCurrency defaults to USD and both monetary settings default to
zero, denying provider execution until configured. `AnalysisSkipPolicy` maps tenant, rate, concurrency and spending
denials to fixed guidance. [Infrastructure budget controls](../../Infrastructure/Ai/README.md) document atomic guards,
reservation/deletion semantics and the operator-approved worst-case request-cost requirement.

M8 configuration keeps existing AI/automatic/provider-egress defaults off. MaximumOutputTokens caps requested provider
output (including reasoning) at 1–8,192 tokens, default 8,192. Common runtime egress policy also rejects timeout outside
5–300 seconds and invalid output limits, even if a custom monitor bypasses Web startup validation. Web validates all
context limits (each 1–131,072) and result retention (1–90 days) independently of enablement. These are bounds, not a
tokenizer or cost estimate; shared cost reservations are separately enforced by IAnalysisBudgetGuard.

`AnalysisResultValidator` implements the Application-owned `IAnalysisResultValidator` port. Every provider response is
untrusted until validation succeeds. The current result contract is version 1: a non-empty summary up to 4,096
characters,
at most 10 non-empty steps up to 2,048 characters each, and at most 20 non-empty evidence references up to 512
characters
each. Empty lists are allowed when evidence or remediation is unavailable. Raw and redacted limits both apply; oversized
or invalid results are rejected whole rather than silently truncated.

All result text goes through the existing `IDiagnosticRedactor`, separately and before JSON serialization. Provenance
identifiers are bounded to 100 characters and rejected if redaction changes them or their syntax resembles a URL or
arbitrary payload. Usage counts are optional non-negative integers. Optional costs require a non-negative decimal within
numeric (18,8) and a three-letter uppercase currency; adapters must supply estimates, never assume current prices.
Unknown usage, cost, model revision and prompt provenance remain null.

`InvalidAnalysisResultException` and `AnalysisProviderUnavailableException` contain fixed safe messages, never a raw
provider body or exception. Supported masking and runtime egress gates are implemented; actual provider approval and
live rollout acceptance remain pending. Context selection
and exact reference
membership are implemented below. Structural validation does not establish that a model's recommendation is correct or
that every
reference resolves to a persisted event. AI remains default-off in `AiAnalysisOptions`.

## Bounded context and evidence (M5)

AnalysisContextSelector processes at most 1,000 durable candidate events. It redacts before grouping, deduplicates event
identity, collapses repeated message/severity/stream groups, and ranks groups by defined severity, failure keywords and
recency. Selected groups render chronologically. Each keeps an actual event GUID or durable order reference, timestamp,
sequence when available, duplicate count and first/last observed timestamp. Messages are capped at 1,024 characters and
may shrink further to fit; oversized early records do not prevent smaller later evidence from being considered.
This is a bounded recent window, not a full-history search; earlier/size omissions and availability notices are
explicit.

AnalysisContextBudget defaults to 24,000 characters, 48,000 encoded UTF-8 bytes and 12,000 conservative token units.
Each encoded provider-input byte costs one token unit, including JSON escaping. This limits the diagnostic input only,
not fixed instructions/schema, model output or total billable request tokens. It is deliberately conservative and not an
exact tokenizer. Values clamp to 1–131,072; budgets too small for a complete record produce empty context and a safe
Skipped result. No token-counting network request is made. Exact full-request counts require provider-specific support;
[official OpenAI token-counting documentation](https://developers.openai.com/api/docs/guides/token-counting)
distinguishes
request structure/schema costs from plain text counts.

When numeric/trace signals exist, log selection uses at most three quarters of each budget, reserving room for
summaries.
Only three known CPU/memory series with finite nonnegative consistent ranges are summarized, using actual returned point
count/min/max/timestamps without container labels or fabricated samples. Trace signals count selected correlated events;
no span export store is queried and no missing span status/duration is invented. Each summary has a generated reference.
Coverage records omitted metric/trace groups too. All selected evidence IDs remain in memory; context is not persisted.

AnalysisEvidence requires exact ordinal membership in the selected reference set for grounded adapter calls and every
worker result. Fabricated, excluded and case-modified references fail the whole result before entity mutation. Empty
reference lists are allowed. Membership demonstrates that a cited item was supplied, not that a model's narrative or
recommendation is correct. Legacy stored results retain structural/redaction validation without invented historical
context membership. Diagnosis-quality interpretation and prompt/egress rollout evaluation remain separate work.

Evidence IDs identify data supplied at invocation; they may cease to resolve after the diagnostic retention window.
Analysis results have their separate 1–90-day retention (default 90) and do not preserve diagnostic snapshots.

`AnalysisEgressPolicy` is the shared fail-closed configuration rule for the initial OpenAI adapter. It requires explicit
provider/regional-processing attestation, matching US/EU base URL, approved owner-account tenant IDs and all three
current
context categories. Bounds and new-result retention (1–90 days) are validated when egress is requested. Database
ownership
and current project consent are enforced through IAnalysisEgressAuthorizer; see the Infrastructure onboarding guide.
Disabled defaults do not require credentials or provider approval and do not affect deployment/log viewing.

AiAnalysisOptions adds LeaseDurationSeconds (default 120, 30–900) and MaximumRecoveryAttempts (default 3, 1–10).
Startup validates these bounds independently of provider enablement; queue/worker boundaries also clamp custom
options sources. These govern interruption recovery, not transient HTTP retry policy. AI remains disabled by default.

AnalysisRetryPolicy returns bounded exponential delays with positive jitter and server minimums, or refuses an
exhausted/unsupported wait. TransientAnalysisProviderException carries a fixed safe message and optional numeric delay,
never a provider body or inner exception. Retry options default to two retries, ten seconds base and 300 seconds max;
startup validates 0–5 retries, 1–300 base and 5–3,600 max with base <= max. Crash recovery remains separately bounded.

DailyProjectLimit defaults to five and is validated at startup in the range 0–1,000. Zero denies new analysis
admissions; active work and stable-ID replay can still be returned under current consent/operator policy. Usage is
recorded in metadata receipts rather than counting deletable result rows; shared tenant/cost controls are also
implemented.

AiAnalysisOptions.MaximumConcurrency is a restart-scoped per-instance processing limit, default one and valid from
one through MaximumSupportedConcurrency (sixteen). Web validates configuration even when AI is disabled; Infrastructure
owns execution slots. This setting does not enable egress or replace the separate shared provider-capacity guard.

AnalysisSkipPolicy owns finite Unavailable, ProjectQuotaExceeded, UnsupportedData, TenantQuotaExceeded, RateLimited,
ConcurrencyExceeded and BudgetExceeded reasons and fixed owner guidance.
Unavailable exceptions carry only this enum; unknown persisted codes read as unavailable. AnalysisEgressPolicy exposes
shared approval requirements; Infrastructure registrations add exact provider routes. The legacy OpenAI helper remains
available without expanding the Application workflow to a provider registry.

## Spending denial codes

Status-aware manual assessment contracts, source classification, frozen ranges and v2 result bounds are documented
in [status-aware assessments](../../docs/status-aware-assessments.md). `AssessmentSelection` canonicalizes request
options; the context selector uses effective focus for ranking and preserves separate container metric groups.
Empty configuration-only context never qualifies as evidence. Existing input/overhead/accounting limits remain.

AnalysisSkipReason appends BudgetNotConfigured, BudgetConfigurationInvalid and BudgetCurrencyMismatch without changing
existing enum values. AnalysisSkipPolicy maps these to finite authored guidance; budget_exceeded explains shared account
reservations and the midnight UTC reset. These are AutoMate reservations, not Azure invoice reconciliation.
