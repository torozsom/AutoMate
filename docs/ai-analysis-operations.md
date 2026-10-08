# AI analysis and security monitoring

This guide defines operator dashboard panels, alert conditions and first responses for the implemented Web telemetry
in both SelfHosted and SaaS. It documents configuration; it does not provision a collector, dashboard, alert rule or
notification destination. AI and provider egress remain disabled until the separate approvals and rollout are complete.

## Collection and access

Configure `OpenTelemetry:OtlpEndpoint` with an approved collector endpoint and restart Web. ServiceName defaults to
`AutoMate`; Environment defaults to the host environment. A collector must accept the configured SDK OTLP protocol and
route logs, metrics and traces to persistent backends. Exporter authentication belongs in protected configuration.
`OpenTelemetry:ExportConsole` only enables console export; it does not provide persistent monitoring.

The private deployment disk gateway is not a general OTLP receiver or trace backend. The supplied Grafana dashboards
show deployment logs and CPU/memory; they do not automatically contain platform security logs or AI workflow metrics.
Create a separate operator dashboard with the platform log, metric and trace data sources. The private Telemetry host
currently uses safe ordinary logging without SDK OTLP exporters; shipping its console logs is a separate operator task.

Filter all panels by the reviewed resource fields: `service.name`, `deployment.environment`,
`automate.hosting_profile` and, when investigating a release, `service.version`. Backends may normalize names and
promote resource fields into labels. Inspect an exported sample to establish that mapping before saving queries.
Use fixed operation/outcome/source dimensions. Keep user, project, deployment, analysis and request GUIDs in restricted
log/trace correlation only; do not promote them into metric labels. No IP addresses, emails or raw payloads are needed.
Apply operator access, retention and SIEM routing policy to internal actor GUIDs as security metadata.

Verify receipt of a safe ordinary application event and a metric sample after configuration. Do not enable AI or make
an LLM request to verify export. Review collector rejection/drop counters, receiver health and latest sample time
alongside application panels. Missing telemetry is unknown, not zero; process restart and absent traffic can also leave
an instrument without samples. Trace sampling and safety omission mean traces are supporting evidence, not a complete
event or billing ledger.

## Dashboard signal catalog

Names below are native OTel instrument/attribute names from source. Metric backends may replace dots, add unit/type
suffixes and expose histogram buckets differently. Translate the formulas to the verified backend schema; they are
definitions, not preconfigured PromQL or LogQL rules.

| Panel                                | Signal and selection                                                                                                                              | Interpretation                                                                                                                                                                                            |
|--------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Authentication outcomes              | Structured audit `Operation=Authentication`, split by `Outcome`; event ID 1000. Also chart final cookie sign-in and fixed login outcome events.   | `Prepared` means an OAuth ticket was prepared, not completed sign-in. Challenges alone do not establish abuse.                                                                                            |
| Authorization refusals               | `Operation=Authorization`, `Outcome=Denied` or `Challenged`; event ID 1001.                                                                       | Denied means forbidden; Challenged means authentication required. Correlate request GUIDs and safe response status, including redirects.                                                                  |
| Request security context             | Completed request audits: `RequestArea`, `StatusCode`, `AuthenticationState`, `RequestId`, optional internal `UserId`.                            | These fields provide request context. They do not prove a business operation succeeded; early redirects and pre-routing requests are outside this audit.                                                  |
| Rate limiting                        | `automate.security.rate_limit.rejections`, split by `security.authentication_state`; audit `Operation=RateLimit`, `Outcome=Denied`, ID 1002.      | Count the counter or audit family once. The callback also emits a separate fixed warning, so counting every related log double-counts refusals.                                                           |
| Redaction volume                     | `automate.deployment.diagnostics.redacted` and `.received`; split by finite deployment source/kind/severity/channel as needed.                    | Redacted counts changed values, not events or bytes. Changed values per received event can exceed one and is not a leakage percentage.                                                                    |
| Diagnostic delivery                  | `automate.deployment.diagnostics.queue.depth`, `.dropped`, `.persistence_failures`, `.delivery_failures`, `.cursor.lag`.                          | Queue depth measures this host's bounded diagnostic dispatcher, not the durable AI queue. Cursor lag is seconds.                                                                                          |
| AI attempt outcomes                  | `automate.analysis.operations` with `analysis.operation=process`, split by `analysis.outcome`.                                                    | completed, failed, canceled, skipped, discarded and retry_scheduled describe attempts; they are not unique analysis totals.                                                                               |
| Provider failures and latency        | `automate.analysis.operations` and `.duration` with `analysis.operation=provider`; split by outcome.                                              | Duration is milliseconds and includes provider invocation plus validation. Failed includes transient and invalid-result failures; skipped and canceled are separate.                                      |
| Committed retries and lost ownership | Operations with `analysis.operation=retry`, `analysis.outcome=retry_scheduled`; renew/release/publish outcomes; corresponding `analysis.*` spans. | Retry scheduling is a successful durable transition. Discarded publication can be expected after cancellation or recovery.                                                                                |
| AI occupancy and acquisition wait    | `automate.analysis.active`; `automate.analysis.queue.wait`.                                                                                       | Active is per-host executing attempts. Queue wait is milliseconds, sampled only on successful claims and excludes scheduled retry backoff. A stopped worker can leave a backlog without new wait samples. |
| Known token usage                    | `automate.analysis.input.tokens`, `.output.tokens` histograms.                                                                                    | Histogram sums measure validated known usage across attempts, including subsequently discarded results. Missing usage is absent; this is not exactly-once cost accounting.                                |
| Durable AI backlog                   | Read-only metadata query below, with query freshness and error state.                                                                             | No AI queue-depth/oldest-age OTel instrument currently exists. Do not substitute cloud deployment queue gauges.                                                                                           |
| Readiness and export health          | HTTP `/health/ready` status plus finite configuration/queue fields; `/health` liveness; collector/backend availability.                           | Readiness checks local configuration and metadata reads, not worker progress, provider connectivity, trigger installation or write permissions.                                                           |

Audit operation/outcome values use PascalCase; analysis metric operation/outcome values use lowercase, with
`retry_scheduled`. Do not apply one casing convention to both. Logs can be correlated through approved GUID scopes and
trace IDs. Spans `analysis.process`, `analysis.context`, `analysis.provider`, `analysis.renew`, `analysis.publish` and
`analysis.retry` locate the failing stage without exposing context or model output.

Preserve Information-level logging for reviewed application/security categories when counting completions, prepared
tickets or unavailable outcomes. A Warning-only filter omits those events and makes success/failure ratios misleading.
Use one canonical audit family for each count; framework sign-in summaries and request completion logs provide context
and must not be added as extra copies of the same authentication operation.

## Durable queue snapshot

Use an operator-managed read-only metadata connection, an approved aggregate view or a restricted scheduled query.
Do not give Grafana customer accounts access to base tables or reuse a database owner credential. Expose aggregate
counts/ages only. Existing hierarchy catalog credentials cannot read these base tables and are not an AI queue source.
This query requires the prepared AI metadata migrations to have been applied through the normal deployment workflow.

The PostgreSQL snapshot follows claim eligibility: unfinished work, unexpired analysis, status Queued=0 or Running=1,
retry due and lease absent/expired. Pending includes delayed retries and live leases; the three buckets are exclusive.
Use a 30–60 second polling interval initially and a short database statement timeout, then review query plans and cost
at production volume. A timeout or query error must show unavailable, not a cached green zero.

```sql
WITH pending AS (
    SELECT w.created_at, w.next_attempt_at, w.lease_until,
           (w.next_attempt_at IS NULL OR w.next_attempt_at <= CURRENT_TIMESTAMP)
               AND (w.lease_until IS NULL OR w.lease_until <= CURRENT_TIMESTAMP) AS eligible,
           GREATEST(w.created_at, COALESCE(w.next_attempt_at, w.created_at),
                    COALESCE(w.lease_until, w.created_at)) AS eligible_at
    FROM deployment_analysis_work_items AS w
    JOIN ai_deployment_analyses AS a ON a.id = w.analysis_id
    WHERE w.completed_at IS NULL
      AND a.expires_at > CURRENT_TIMESTAMP
      AND a.status IN (0, 1)
)
SELECT COUNT(*) AS pending_total,
       COUNT(*) FILTER (WHERE eligible) AS eligible_work,
       COUNT(*) FILTER (WHERE lease_until > CURRENT_TIMESTAMP) AS leased_work,
       COUNT(*) FILTER (
           WHERE next_attempt_at > CURRENT_TIMESTAMP
             AND (lease_until IS NULL OR lease_until <= CURRENT_TIMESTAMP)
       ) AS delayed_retries,
       COALESCE(EXTRACT(EPOCH FROM (
           CURRENT_TIMESTAMP - MIN(eligible_at) FILTER (WHERE eligible)
       )), 0) AS oldest_eligible_seconds
FROM pending;

SELECT COUNT(*) AS pending_wakeups,
       COALESCE(EXTRACT(EPOCH FROM (CURRENT_TIMESTAMP - MIN(created_at))), 0)
           AS oldest_wakeup_seconds
FROM failed_deployment_analysis_events
WHERE completed_at IS NULL;
```

Completed wakeup markers are retained to prevent repeat automatic admission; their total row count is not backlog.
Live leases and future retries must not be counted as currently eligible work. Snapshot age starts when work becomes
eligible after retry/lease deadlines; it is not identical to the queue-wait histogram. This is one cluster snapshot:
if several monitors publish the same counts, use one source or `max`, not `sum`. Sum per-process active attempts and
diagnostic queue depths only across distinct process streams; preserve collector-managed stream identity without
adding customer identifiers. No snapshot query changes queue ownership or deployment state.

## Initial alert policy

The values below are starting points for an internal pilot, not measured SLOs. Establish a normal-traffic baseline and
adjust them with the operator responsible for each environment. Evaluate rates across a five-minute window, retain
raw counts beside ratios and require sufficient observations. Counter rates must handle process resets; histogram
percentiles must aggregate bucket rates before computing a quantile, not average per-instance percentiles.

| Alert and routing                               | Initial condition                                                                                                                                                                                                                                 | First response                                                                                                                                                                   |
|-------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Authentication anomaly → security triage        | At least 20 Authentication Denied/Failed events in five minutes and at least three times the comparable normal baseline, sustained five minutes. Where only request-audit data is available, label 401/403 Authentication-area counts as a proxy. | Check auth provider health, release/config changes and final sign-in events. Use restricted request/actor correlation; anonymous events cannot identify an account or source IP. |
| Authorization anomaly → security triage         | At least 20 Authorization Denied events in five minutes, or a sharp baseline increase; chart Challenged separately.                                                                                                                               | Check expected ownership/role refusals and request status. Investigate repeated denied access without changing authorization to silence the alert.                               |
| Rate-limit pressure → application operator      | At least 50 rejections in five minutes, sustained five minutes, split authenticated/anonymous.                                                                                                                                                    | Separate deliberate abuse from retry loops, health probe cadence and normal demand. Review partition behavior before changing limits.                                            |
| Unusual redaction → security/application triage | At least 100 received events in five minutes and changed-values/received-events greater than three times baseline, sustained ten minutes.                                                                                                         | Inspect approved source/kind and redacted evidence only. Repeated `[REDACTED]` confirms masking, not detection of every unknown secret or PII type.                              |
| Diagnostic loss → on-call                       | Any increase in dropped events or persistence failures, sustained two minutes; delivery failures separately warn for five minutes.                                                                                                                | Check bounded-buffer pressure, disk gateway/storage and slow terminal clients. Use existing storage recovery procedures.                                                         |
| Eligible AI backlog → on-call                   | Eligible work exists and oldest eligible age exceeds five minutes for five minutes; wakeups older than two minutes for five minutes also warn.                                                                                                    | Verify worker/dispatcher host lifecycle, database schema/permissions, lease clocks and processing capacity. Delayed retries alone do not page.                                   |
| AI provider failure ratio → on-call             | Failed provider operations / (completed + failed provider operations) exceeds 20%, with at least ten operations in ten minutes, sustained five minutes.                                                                                           | Correlate provider-stage spans and safe failure classes. Check timeout, egress policy, consent and retry outcomes; do not resend old diagnostic context manually.                |
| Terminal AI failures → application operator     | Failed process operations rise persistently; review saved terminal status using authorized metadata access.                                                                                                                                       | Separate retry_scheduled from terminal failed, and skipped/canceled from errors. Attempt metrics do not replace final unique-analysis counts.                                    |
| Provider latency → application operator         | Provider-operation p95 exceeds 80% of configured TimeoutSeconds × 1,000, with at least ten observations in ten minutes, sustained ten minutes.                                                                                                    | Separate successful and failed latency, then inspect provider-stage traces. Check context size and provider health before raising timeout/concurrency.                           |
| AI readiness unavailable → on-call              | `/health/ready` fails three consecutive 30-second probes outside a declared maintenance window.                                                                                                                                                   | Distinguish Disabled/Available (healthy), intentional EgressDisabled, invalid options and unavailable metadata. Use `/health` for restart decisions.                             |
| Monitoring blind spot → telemetry operator      | Collector unavailable, exporter drops, query failures, or expected host telemetry stale for five minutes while the host should be running.                                                                                                        | Restore collection/backends and label dependent panels unknown. An idle AI metric is not itself proof that export failed.                                                        |

Group alerts by environment, hosting profile and alert family; deduplicate replicas and rate-limit notifications.
Route security anomalies to the security operator and queue/storage/provider incidents to application on-call. Keep
notifications to finite condition, UTC window, count/ratio and links to restricted dashboards/runbooks. Do not include
diagnostic context, provider responses, credentials or actor IDs in notifications. Record maintenance suppressions and
restoration evidence; intentional egress shutdown should suppress provider-readiness noise, not storage/export alerts.

## Investigation and recovery

1. Verify time window, environment, release and telemetry freshness. Check readiness and the metadata snapshot before
   interpreting an empty chart. Distinguish expected AI-disabled operation from degraded enabled configuration.
2. Use safe Operation/Outcome logs and `analysis.*` traces to locate admission, claim, context, provider, lease or
   publication failure. Check fresh consent/policy and bounded retries; preserve lease fencing and completion markers.
3. If unexpected data transmission is suspected, disable `AiAnalysis:ProviderEgressEnabled` through the operator's
   configuration process and verify subsequent calls are denied. Environment-backed changes require restart;
   reloadable sources deny subsequent attempts and cancel active OpenAI I/O once each process observes the change.
   Already-sent remote requests cannot be recalled. Do not alter
   deployment execution or retain an unredacted diagnostic copy for investigation.
4. Restore the failed dependency/configuration, verify collector/readiness/snapshot recovery and observe a legitimate
   authorized request when rollout permits. A health probe must never invoke an LLM. Record incident and release
   details, alert changes and safe recovery evidence under the operator's retention policy.

## Acceptance before enabling alerts

Check backend name/unit/attribute mappings against received safe samples. Use synthetic log/metric fixtures in an
isolated monitoring environment to exercise alert firing, volume gates, reset handling, missing-data behavior,
maintenance suppression, recovery and routing. Exercise queue SQL on a disposable migrated PostgreSQL database with
empty, eligible, leased, delayed, expired, canceled and completed rows. Verify zero changes to source metadata and
restricted database permissions. These are operator acceptance steps, not claims of a live-stack validation here.

Per-owner-account admission/rate limits, shared provider concurrency and conservative attempt-cost reservations are
implemented; [configuration and migration](../Infrastructure/Ai/README.md) describe their boundaries. USD spending
defaults to zero. Observe Analysis/Denied events and fixed tenant_quota_exceeded, rate_limited, concurrency_exceeded
and budget_exceeded result codes through authorized reads. Budget records survive project/result deletion and retain
ninety days of metadata. An operator can query current-day sums of `reserved_cost_units` from
`ai_analysis_budget_entries` with `is_provider_attempt=TRUE`, grouped by currency, through restricted aggregate views.
Divide by 100,000,000 for currency units. These sums are conservative reserved spend, not actual invoices; keep actor
IDs out of metrics and notifications. Alert on policy-denial trends and sustained provider-capacity denial alongside
queue progress. Provider quality, exactly-once billing and live rollout acceptance remain separate plan items. Token
telemetry cannot certify billed spend or replace the operator's worst-case per-attempt cost bound.

## Staged feature enablement and shutdown

`AiAnalysis:ApprovedTenantIds` is the explicit owner-account rollout cohort. All projects for those accounts share the
cohort, but each project still requires current diagnostic-egress consent. There is no wildcard or percentage rollout.
`Enabled` is the global feature flag, `AutomaticAnalysisEnabled` adds failure-trigger opt-in, and
`ProviderEgressEnabled` is the independent provider kill switch. All default to false. Approval flags attest to actual
approvals; setting a flag does not provide a contract, approved region or permission to send data.

| Stage                    | Required state and promotion evidence                                                                                                                                                                                                                                                                                                                                                                                                                                |
|--------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Off / local verification | Keep all three flags false and both cost settings zero. Apply reviewed metadata migrations through the normal deployment workflow, replace old worker binaries, run credential-free tests and verify metadata readiness. Review redaction with anonymized samples locally. No provider call is authorized.                                                                                                                                                           |
| Internal manual pilot    | Resolve provider/account/model/region and data-processing approvals first. Set a small explicit internal owner cohort, obtain consent for each sample project and configure protected credentials, approved categories, bounded inputs/output, limits and an accurate positive conservative USD budget. Enable `Enabled` and `ProviderEgressEnabled`; leave `AutomaticAnalysisEnabled=false`. Deliberately request manual analyses of anonymized sample deployments. |
| Internal automatic pilot | After manual acceptance, enable `AutomaticAnalysisEnabled` for the same cohort and consenting projects. This is a global additional opt-in, not a separate per-project automatic preference. Only new failed transitions qualify; there is no historical backfill.                                                                                                                                                                                                   |
| Gradual expansion        | Add explicitly reviewed owner IDs in small batches. Repeat acceptance and monitor each batch before adding another. Expansion cannot bypass consent, regional approvals or spend controls.                                                                                                                                                                                                                                                                           |

Before promotion, record the release/configuration revision, cohort approval, consent evidence, quality and incorrect
remediation feedback, redaction review, provider latency/errors/retries and reserved spend alongside actual provider
usage. Use the signals above and authorized samples; keep customer data out of metrics/notifications. Owners must
review suggestions; AutoMate does not execute them. Halt promotion on unexplained egress, redaction failures,
unsupported suggestions, budget discrepancies or stalled work. This is an operator acceptance path; implementation
has not performed a live pilot, granted provider approval or assessed model quality.

For shutdown, set `AiAnalysis:ProviderEgressEnabled=false` in effective operator-controlled configuration on **every
replica**. Use a reloadable source for shutdown without restart. Standard Web appsettings JSON supports reload; update
the deployed environment-specific file atomically, preserving unrelated settings and valid JSON. Keep credentials in
protected configuration. Environment variables and command-line arguments take precedence over JSON and do not
reload: `AiAnalysis__ProviderEgressEnabled=true` would mask a lower-priority JSON shutdown. Establish and test the
effective flag source before the pilot. Environment-only installations must replace/restart processes with the flag
false. File detection/configuration propagation takes time: cancellation is immediate **after local reload**, not an
instantaneous cluster barrier.

Both OpenAI and Azure OpenAI adapters subscribe during each request and cancels linked HTTP headers/body I/O on any AI
options snapshot
change, including cohort removal, feature disablement and route/model changes. The worker records Skipped/unavailable
without a transport retry. Fresh policy checks deny new admissions, queued work and retries. Changing only
`AutomaticAnalysisEnabled` leaves new manual requests available under other gates but conservatively cancels existing
requests. Subscriptions are disposed on completion. Future provider adapters must implement equivalent in-flight
cancellation before rollout approval; the registered production adapters are direct OpenAI and Azure OpenAI (API-key
mode).

Verify `/health/ready` reports `EgressDisabled` (Degraded/503 with `Enabled=true`) on each replica and inspect safe
Analysis/Denied and worker outcomes. Readiness cannot prove the provider received no bytes. To stop the feature
entirely, also set `Enabled=false`; disabled readiness is healthy when metadata is accessible. Deployment execution
and diagnostics continue independently; existing authorized analysis reads, cancellation/deletion and retention remain
available. Shutdown does not delete metadata or refund reservations. Remote processing may continue after local
cancellation, already-sent data cannot be recalled, and a final-check/transmission race remains. Use provider-side
credential revocation/network controls when required by an incident and reconcile remote usage before resuming.

After remediation, restore the last reviewed configuration through the manual pilot first. Skipped results are not
automatically replayed; a deliberate new request is required. This change needs no additional migration; previously
prepared metadata migrations remain unapplied here.

## Related implementation and operations

- [Architecture, provider onboarding, retention and metadata upgrades](ai-analysis.md)
- [Internal pilot acceptance record](ai-analysis-rollout.md)
- [Durable execution and rollout decisions](adr/0003-ai-analysis-execution-and-rollout.md)

- [Analysis metric and audit contracts](../Application/Diagnostics/README.md)
- [Platform export safety](../Web/Observability/README.md)
- [Health probe semantics](../Web/Configs/README.md)
- [Queue, leases, retries and provider policy](../Infrastructure/Ai/README.md)
- [Deployment storage and retention operations](deployment-telemetry.md)
- [Existing local Grafana deployment dashboards](../deploy/telemetry/README.md)
