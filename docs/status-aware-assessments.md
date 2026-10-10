# Status-aware deployment assessments

Manual assessments are available for Starting, Failed, Running and Stopped deployments on both Project Details and
Deployment Details. Automatic execution remains failure-only and requires the existing owner consent and operator
approval. No Azure settings or secrets change: the installation retains its $0.07 attempt reservation, $50 daily
account ceiling, disabled HTTP retries, and existing accounting and retention.

## Owner controls

Choose Automatic, Startup assessment, Failure diagnosis, Runtime overview or Historical review. Automatic resolves
when the worker collects context from persisted status: Starting → Startup; Failed → Failure diagnosis; Running →
Runtime overview; Stopped → Historical review. Overrides change emphasis, never the recorded status/outcome.
Historical assessments cannot claim that a stopped application is currently running.

Choose container build, web, database, GitHub Actions, Azure platform/system, deployment events and Other logs.
Recorded web/database channels can be selected individually. Azure application stdout/stderr belongs to Web;
Azure system/revision output belongs to Azure platform/system. Classification uses recorded typed metadata, never
message prose or current project configuration. Unclassifiable legacy output remains Other. Missing output and
sources unsupported by the recorded deployment are labeled explicitly.

Metrics have an independent toggle and container selection. Null container selections mean all recorded channels;
empty selections mean none. Select Last 15 minutes, Last hour, Last 24 hours, Deployment lifetime or Custom UTC range.
Relative ranges end at collection time; lifetime ends at the recorded last activity for Failed/Stopped deployments.
Automatic range uses Last hour for Starting/Running and Deployment lifetime for Failed/Stopped. A range is limited
to 365 days; longer lifetimes explicitly record truncation to their latest 365 days. Custom ranges cannot end in the
future. Save choices to reuse them for this deployment on either page. Submitted controls are frozen while pending.

## Contracts and evidence

`AssessmentSelection` is the typed request contract. Owner-authorized preferences are stored on Deployment separately
from immutable analysis/receipt options. Canonical options make identical request-ID retries idempotent; changing
options with the same request ID returns a conflict. The worker freezes absolute range, collection timestamp,
effective focus and observed status/outcome. Preferences never rewrite earlier requests or results.

Private archive assessment queries carry tenant/project/deployment, selected sources/containers and absolute UTC
bounds. Archive and Loki source filtering occurs before candidate limits; Mimir container filtering occurs before
interval aggregation. Reads remain bounded and deduplicated by event identity. Retained legacy evidence is merged;
durable backfill imports still-available older backend diagnostics. Expired diagnostics cannot be recovered.
Selected diagnostics are mandatory: empty selections, unavailable evidence or configuration-only context skip before
reservation or provider invocation. Partial availability/omission is explicit in the bounded context.

The recorded configuration whitelist contains provider, runtime, environment name, exposed port, public flag,
region and image. Values are bounded and centrally redacted. It excludes environment-variable values, credentials,
source paths and reconstructed legacy settings. Configuration accompanies deployment diagnostic context under
existing consent; it does not widen provider routing or authorize a new destination.

Failure diagnosis ranks errors first. Other modes favor representative selected channels and recent operation.
Metric groups remain separate per container/name/unit and include interval means, extrema, covered intervals and
first-to-last changes. Archived memory-limit metrics support memory comparisons; no CPU limits, traffic expectations
or health thresholds are inferred. Low utilization alone is not proof of health.

## Result compatibility and API

Schema v2 presents Overview, Observations, Metrics assessment, Potential issues, Recommendations, Limitations and
exact selected evidence references. Empty sections receive explanatory presentation. Every authored field is
validated, bounded and redacted. Each added section allows six items of at most 768 characters. Existing overview,
recommendation and reference bounds still apply. Trusted instructions and schema fit the existing overhead allowance;
input limits and 4,096 output tokens remain unchanged. Older saved results retain their original layout with unknown
selection/status metadata. Direct legacy adapter calls retain their v1 response contract; production workers supply
an effective assessment kind and require v2.

Authenticated routes:

- GET `/api/deployments/{deploymentId}/analyses/preferences`
- PUT the same route with an `AssessmentSelection` body
- POST `/api/deployments/{deploymentId}/analyses` with `{ requestId, selection }`

Mutations require antiforgery validation. Owner identity comes from authentication; database and private Telemetry
authorization recheck ownership and current managed-telemetry consent. Conflicting request IDs return HTTP 409.
Existing cancellation, five-second polling, route/disposal fencing, admission receipts and accounting stay intact.

## Rollout and verification

1. Back up PostgreSQL and the persistent telemetry volume; stop old Web/workers during upgrade.
2. Apply `20261009091530_StatusAwareAssessments`. It adds nullable preference/options/provenance/sections columns,
   preserving deployment identities, existing analyses, request receipts and spending records.
3. Deploy filter-capable private Telemetry before updated Web/workers. Preserve its existing archive volume and
   single-writer deployment; the permanent-history backup/storage requirements in ADR 0004 still apply.
4. Verify owner-authorized context catalog/filtered reads, consent and provider-free empty-context skipping before
   initiating an assessment. Check both current and historical pages.

Tests cover mappings/overrides, range bounds, canonical requests, preference isolation, typed source categories,
filtering before archive/backend limits, redaction, exact evidence, v1 compatibility and v2 adapter contracts.
The isolated Compose integration suite exercises real PostgreSQL/Loki/Mimir without paid provider calls.
