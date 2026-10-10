# Infrastructure Tests

Ai/AnalysisBudgetTests uses independent SQLite contexts to verify account quotas across projects, atomic reservations,
shared tenant/global provider slots, rolling/time boundaries, project/result deletion survival, currency denial,
retry/recovery charges and transaction failure cleanup. Persistence fixtures verify disabled spending finishes as
Skipped/budget_exceeded without resolving/calling a provider. Production PostgreSQL lock execution is not inferred
from SQLite coverage; the metadata migration SQL/model are separately reviewed without applying a live migration.

Ai/AnalysisBudgetPostgresTests executes ordered migrations in generated schemas and verifies real owner/advisory locks,
quota/spend after deletion, live provider capacity, same-lease/recovered reservations, canceled lock waits and consuming
receipt backfill. Run `./deploy/verification/Test-AiPostgres.ps1`;
see [isolated verification](../deploy/verification/README.md).
Default runs skip these tests without an explicitly isolated AUTOMATE_AI_TEST_DB. Six PostgreSQL checks, including the
production failure-trigger test, pass locally; they do not certify production encryption, HA or provider access.

Ai/AnalysisReadinessTests exercises real empty SQLite metadata queues with bounded SELECT-only probes, current policy
and credential reload, invalid options, missing queue/analysis/wakeup tables and cancellation at relational execution.
A provider whose constructor throws verifies metadata readiness never resolves an adapter or makes a provider call.

OpenAiAnalysisProviderTests additionally checks configured output-token limits in the actual synthetic HTTP request and
zero transport calls for invalid runtime timeout/output settings, including custom monitors bypassing startup.

Ai/AnalysisConsentTests verifies exact-project consent persistence/revocation through fresh SQLite contexts, sibling
isolation, owner authorization, application/project membership and missing configuration. Remote projects can create
their missing configuration during explicit consent editing; local missing configuration remains denied. It uses no
provider traffic.

AnalysisPersistenceTests additionally verifies actual worker telemetry outcomes for success/failure/invalid/skip/retry/
cancellation, provider-reported usage and balanced active slots using scoped activity/meter listeners.
DockerCollectorTests
verifies an initial container-creation race waits/retries without warnings while permission failures stay visible.

Automated, credential-free verification for deployment diagnostics and external-adapter normalization.

Run from the repository root:

```powershell
dotnet test AutoMate.slnx --no-restore
```

These tests use fake HTTP responses and temporary SQLite databases. Default runs do not contact Docker, GitHub, Azure,
or an
OpenTelemetry collector; the explicitly enabled Docker smoke tests are described below.

Telemetry integration tests additionally use an explicitly configured disposable PostgreSQL/Loki/Mimir stack. They are
skipped without `AUTOMATE_TELEMETRY_TEST_DB`. See [telemetry verification](../docs/deployment-telemetry.md).

## Deployment telemetry update

Both SelfHosted and SaaS use the private Telemetry disk gateway for new deployment logs and metrics. PostgreSQL payload
writes are rejected at application startup; legacy reads and draining of existing outbox rows remain available. The
gateway confirms durable checksummed writes before cloud checkpoints advance. Tenant-scoped v2 history, deployment
revision recovery and weighted daily project analytics are documented in [the rollout guide](/docs/saas-telemetry.md).
Detailed data expires after 30 days; daily statistics after 365 days. See the root navigation.md for new module entry
points.

Local Docker diagnostics also have an opt-in real-daemon smoke test. Default runs skip it. Set `AUTOMATE_DOCKER_SMOKE=1`
and `AUTOMATE_DOCKER_SMOKE_IMAGE=busybox:1.37` (or another already-present image with `sh`/`printf`) to verify owned
lifecycle/stdout/stderr collection, redaction and exclusion of an unrelated container. The test creates no ports/volumes
and cleans up only its disposable container IDs. Supervision tests use SQLite for deployment/consent metadata only.

`Ai/AnalysisResultTests`, `OpenAiAnalysisProviderTests` and `AnalysisPersistenceTests` verify bounded result validation,
central redaction, safe HTTP/error handling, real SQLite metadata persistence, legacy safety and authorized retrieval.
All credentials/envelopes are synthetic and no LLM request is made.

OpenAiAnalysisProviderTests additionally uses real options/configuration reload to disable the feature or provider
egress during synthetic headers/body waits. Active HTTP I/O cancels as Unavailable and subsequent calls never send.

`AnalysisPersistenceTests` also covers bounded retention cleanup, every expired state, owner deletion, active conflicts,
queue cascades, immediate expiry exclusion, cancellation and in-flight completion after expiry/deletion.

`AnalysisPersistenceTests` also covers bounded retention cleanup, every expired state, owner deletion, active conflicts,
queue cascades, immediate expiry exclusion, cancellation and in-flight completion after expiry/deletion.

Analysis persistence tests also capture actual worker log state/scopes for completed, invalid, failed and unavailable
provider paths. They verify safe finite outcomes, deployment/analysis correlation and omission of injected secret text.

Pipeline, GitHub and Azure behavior tests also inspect diagnostic/provider spans for trace continuity and GUID
correlation while retaining workflow ordering and revision-isolation assertions.

CloudFailurePrivacyTests verifies that recognized customer-visible cloud failure categories cannot copy an untrusted
exception suffix.

M5 masking tests cover JSON/environment/connection-string credentials, cookies/authorization, URLs, PEM/JWT/provider
formats, control-sequence bypasses, keys/source/metric metadata, repeated masking and fail-closed input bounds.
Synthetic
HTTP request tests inspect provider context and verify oversized context never sends. SQLite history fixtures verify
current masking for legacy and Loki readback without modifying stored rows or introducing telemetry persistence writes.

DiskTelemetrySpoolTests verifies direct admission masking, mutable attribute isolation, masked restart replay and
current-policy backlog reads without rewriting checksummed disk bytes. A real publisher/MeterListener regression
checks malformed external enum values cannot create arbitrary diagnostic metric labels.

AnalysisContextTests covers severity/relevance ranking, deterministic redacted duplicate groups, Unicode/escaped budget
limits, complete JSON, numeric/trace summaries, omissions, cancellation and reference membership.
AnalysisPersistenceTests
exercises actual builder query scoping, managed-storage consent, optional Mimir outages and fabricated evidence failure
before writes. OpenAiAnalysisProviderTests verifies escaped hostile delimiter data, grounded adapter rejection and
encoded
budget rejection before HTTP. Existing legacy/Loki replay tests verify timestamp/severity/sequence/trace metadata.

AI policy regressions use explicit synthetic tenant/provider/region/category approvals. They verify queued and
mid-context consent revocation, operator configuration reload, scope denial, matching regional routes, and configurable
metadata retention. Hosting-profile tests verify shared registration and fail-closed option validation; no LLM call
occurs.

AnalysisQueueLeaseTests exercises the production queue through independent SQLite file connections: concurrent
claims, renewal/release ownership, expiry recovery, legacy claims and terminal exclusion. AnalysisPersistenceTests
adds late stale-provider fencing, bounded interruption exhaustion, renewal failure and host cancellation/release.
All providers are synthetic. PostgreSQL migration SQL is reviewed; real PostgreSQL concurrency requires integration
configuration and is not inferred from SQLite coverage.

Durable retry tests verify future eligibility across independent SQLite connections, concurrent deadline claims,
fresh context/policy on later attempts, separate recovery counts, exhaustion/expiry and stale failure fencing. Adapter
fixtures cover transient/permanent HTTP classes, temporary versus quota/billing 429 codes, bounded malformed bodies,
safe transport errors/cancellation and Retry-After date/delta hints. AnalysisRetryPolicyTests verifies delay bounds.
All HTTP handlers are synthetic; no live provider is contacted.

Owner cancellation regressions cover initial/delayed work with AI disabled, inaccessible/expired identities, terminal
result preservation, late completion and transient failure, context/heartbeat cancellation and safe owner audit events.
A command interceptor forces queue retirement failure to verify transaction rollback. Independent SQLite connections
verify concurrent idempotent cancellation/restart exclusion; deletion/retention tests now include Cancelled.

AnalysisAdmissionTests uses independent SQLite file connections and the real metadata authorizer to verify concurrent
legacy/stable-ID requests, active aliases, terminal replay, cross-deployment daily quota, UTC rollover,
deletion/retention
and receipt caps. A work-insert failure verifies transaction rollback and safe scoped-context reuse. Policy denial/empty
IDs/caller cancellation cannot admit work. Fixtures never load diagnostics or contact a provider. PostgreSQL row-lock
execution/backfill remains an integration-environment check; reviewed SQL does not substitute for running that stack.

AnalysisAdmissionTests includes automatic restart/concurrent dispatch, result/receipt deletion, shared manual quota and
coalescing, policy denials, transactional rollback, bulk/repeated failure capture and early local cancellation with the
actual deployment ID. SQLite uses equivalent test triggers for application semantics.
FailedDeploymentTriggerPostgresTests
executes the exact production PostgreSQL capture SQL in a uniquely generated disposable schema when AUTOMATE_AI_TEST_DB
explicitly targets an isolated test database. It applies no application migrations and never contacts a database by
default.

AnalysisWorkerConcurrencyTests runs the hosted worker with independent file-backed SQLite connections and synthetic
blocking providers. It verifies exact slot filling/no preclaim, slot refill after success/failure, distinct processing
contexts, shutdown cancellation/release and distinct production queue claims across two worker instances. No external
provider is contacted. HostingProfileRegistrationTests validates concurrency bounds (including 1 and 16) with AI off.

AnalysisProviderSelectionTests verifies missing/unknown/disabled/mismatched routing without adapter construction,
a second synthetic registered provider, reloaded policy during resolution, duplicate identities and mismatched response
provenance. Admission tests verify concurrent stable-key skipped results with no work/quota charge, deletion replay,
owner scoping and hostile saved fields. Persistence tests prove empty context skips before provider invocation. Existing
OpenAI transport and middleware checks remain credential-free; no live provider is called.

## Azure OpenAI adapter acceptance

`ResponsesAnalysisProviderContractTests` runs the same transport/redaction/context/evidence/provenance/error and
headers/body shutdown cases through both `OpenAiAnalysisProviderTests` and `AzureOpenAiAnalysisProviderTests`.
Azure-specific fixtures cover exact resource URLs, API-key isolation/invalid configuration, numeric 429 retry hints and
real nested credential/resource reload. All use synthetic HTTP and contain no usable provider credentials.

Budget regression coverage now includes distinct missing/invalid/currency denials, sequential local/cloud reservations,
UTC reset and a real relational-query configuration reload without a charge. Worker persistence tests assert denied work
never calls the provider and no Skipped analysis emits a Completed audit. Shared safe logging is used by the worker
fixture.

## Deployment history update (2026-10-08)

DiskDeploymentArchiveTests covers old-event restart replay, event identity, bounded paging/search, current redaction,
metric interval import, checksums, incomplete files and deletion tombstones. Admission regressions cover manual
statuses, retained results after 121 days, multiple runs and outcome survival after stopping.

DeploymentJobWorkerTests deterministically delays queue cancellation beyond active-job completion and verifies that
shutdown waits for the read before iterator disposal, with no faulted worker task.
