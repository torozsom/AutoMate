# Deployment analysis ports

`IAnalysisBudgetGuard` gates one provider attempt with current ownership, shared provider concurrency and a durable
conservative maximum-cost reservation. Its finite denial becomes a user-visible Skipped result. The worker calls it
after fresh egress/context checks and before resolving the provider; retries and recovery use new ownership tokens.
See [shared budget semantics and limits](../../../Infrastructure/Ai/README.md). It is not a billing reconciliation API.

`IDeploymentAnalysisReadiness` returns only finite configuration state and queue metadata availability. The scoped
Infrastructure implementation checks current policy, local credential presence and bounded read-only metadata access.
It never resolves an LLM adapter, claims work, loads diagnostic context or grants tenant/project consent. Cancellation
reaches database reads. Web maps this port to the five-second `/health/ready` check independently of `/health` liveness.

`ILlmAnalysisProvider` consumes already-redacted in-memory context and returns an untrusted `LlmAnalysisResponse`.
The response adds optional requested/returned model provenance, explicit model revision, AutoMate prompt/schema
versions,
usage counts and cost estimates. Existing constructors remain compatible. Provider SDK models and raw envelopes never
cross this boundary.

`IAnalysisResultValidator` returns a safe independent copy or rejects the whole result. Both the provider adapter and
persistence consumer enforce it. `IDeploymentAnalysisService` exposes owner-authorized result views with additive
optional
provenance/usage fields. Legacy metadata remains unknown, and retrieval does not invoke a provider.
`IDeploymentAnalysisQueue` returns detached analysis/deployment IDs, an ownership token, lease expiry and attempt
number.
Atomic claims, token-fenced renewal and release support bounded interruption recovery. Context is never queued.
Durable retries, owner cancellation, manual/automatic admission and per-instance processing concurrency are implemented.
Cross-instance tenant/provider limits and conservative spend reservations are implemented in M8.

See [result policy](../../Ai/README.md) and [adapter/worker behavior](../../../Infrastructure/Ai/README.md).
The [onboarding guide](../../../docs/ai-analysis.md) maps every port to its implementation and documents upgrades,
operator controls, independent retention and the pending provider/pilot approvals.

`IDeploymentAnalysisService.DeleteAsync` authorizes owner/deployment/analysis together, deletes terminal or expired
results
and cascading queue metadata, and returns Deleted, NotFound or InProgress. Foreign IDs are indistinguishable from
missing
IDs. Owners can cancel unexpired active work before deleting it. Expired analyses are excluded from readback.

IDeploymentAnalysisContextBuilder returns DeploymentAnalysisContext with selected Text and EvidenceReferences. The Web
composition registers the Infrastructure reader; selection/budget policy remains in Application/Ai. Context and the
reference catalog are ephemeral and are not added to queue metadata or PostgreSQL diagnostic storage. LlmAnalysisRequest
adds optional AllowedEvidenceReferences: the worker always supplies a detached read-only list and enforces membership
again before persistence. Null retains shape-only compatibility for standalone legacy adapter callers; it does not
approve provider egress or provide a grounded worker result.

`IAnalysisEgressAuthorizer` rechecks current operator policy, deployment ownership and persisted project consent. Worker
and adapter invoke it independently. LlmAnalysisRequest adds optional DeploymentId/Trigger for source compatibility;
the real adapter requires a deployment scope, and neither an evidence catalog nor a trigger grants egress permission.

DeploymentAnalysisWorkItem additionally carries ProviderRetryCount. Attempt now counts acquisitions since the last
scheduled transient retry; interruption recovery and provider retry budgets are independent. ILlmAnalysisProvider
adapters may throw the Application-owned TransientAnalysisProviderException; invalid/permanent failures remain terminal.
Each worker invocation rebuilds ephemeral context and rechecks egress rather than persisting a previous request.

IDeploymentAnalysisService.CancelAsync authorizes owner/deployment/analysis inside the write, requires unexpired
metadata and transitions Queued/Running (including future retries) to Cancelled. It returns Cancelled idempotently,
NotFound for missing/foreign/expired IDs, or AlreadyFinished for Completed/Failed/Skipped. Cancellation works with AI
disabled and does not require egress consent. Existing deletion remains separate; canceled results are owner-deletable.
The Cancelled view contains only fixed wording and identity/state/timing, never partial guidance or unvalidated
provenance.

RequestManualAsync adds a stable GUID requestId overload, scoped by owner and deployment. Reusing an accepted ID
returns its live prior analysis; deleted/expired results are not recreated within the ninety-day receipt window.
The original overload remains available and coalesces active work. Every admission/replay still checks current policy.
New work consumes durable daily project allowance; active aliases and replay do not. No context is persisted in
receipts.

DeploymentAnalysisRequestResult.Accepted=false can include a terminal Skipped view for an enabled, consented request
that exceeds project quota or lacks egress approval. Such admission creates no work and consumes no allowance.
Ownership/
consent/global-disable denials remain empty. Stable IDs replay skipped results. Skipped readback uses fixed guidance,
known reason codes and no saved provenance or partial result fields. ILlmAnalysisProvider remains provider-neutral;
Infrastructure registry routing does not change Application workflow or UI contracts.

Spending denials distinguish absent/invalid amounts, currency mismatch and genuinely exhausted account reservations.
IAnalysisBudgetGuard keeps its existing nullable finite-reason signature. No metadata schema migration is needed.

## Status-aware assessments (2026-10-09)

Typed `AssessmentSelection` admission, owner-authorized per-deployment preferences and optional `AssessmentProvenance`/
`AssessmentSections` extend the existing ports. Choices are canonicalized and stored separately on receipts/results.
Different options under the same request ID return Conflict. Context remains ephemeral; only selection/status/window
metadata and validated authored results persist. Skipped results may include collection provenance, with fixed safe
guidance. See [contracts and rollout](../../../docs/status-aware-assessments.md).
