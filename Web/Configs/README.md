# Configs

M8 shared limits use owner accounts as tenants. Startup validates daily tenant quota, rolling admission rate, shared
provider concurrency, exact eight-decimal cost budgets and uppercase currency even while AI is disabled. Defaults are
100 admissions/day, 10/minute, two provider attempts per tenant and sixteen globally. USD budgets default to zero;
execution needs a configured daily allowance and operator-approved worst-case per-attempt amount. See
[limits and migration semantics](../../Infrastructure/Ai/README.md). Both profiles register scoped
`IAnalysisBudgetGuard`.
Readiness verifies the new accounting schema and treats enabled egress without a positive sufficient spending
allowance as Unavailable/503. This does not contact the provider or inspect private account usage.

The [operator monitoring guide](../../docs/ai-analysis-operations.md) documents export setup, readiness alert handling,
security/AI panels, initial thresholds and monitoring acceptance. Readiness alone does not establish worker progress;
the guide includes a separate read-only queue snapshot and telemetry freshness checks.

## Liveness and AI readiness

Both hosting profiles register `AnalysisReadinessHealthCheck` through the Application readiness port with a five-second
timeout. `/health` remains lightweight liveness and excludes the AI check. `/health/ready` runs the tagged AI check and
returns uncached JSON with only `status`, `configuration` and `queue`. Exceptions, descriptions, provider identifiers,
endpoints, tenant IDs, credentials and queue payloads are excluded.

| Configuration / queue                                            | Health status                        | HTTP |
|------------------------------------------------------------------|--------------------------------------|------|
| AI disabled, metadata readable                                   | Healthy; Disabled / Available        | 200  |
| Approved routing and local credential present, metadata readable | Healthy; Ready / Available           | 200  |
| AI enabled, provider egress disabled                             | Degraded; EgressDisabled / Available | 503  |
| AI enabled, missing provider approval/route/credential           | Degraded; Unavailable / Available    | 503  |
| Invalid reloaded options                                         | Unhealthy; Invalid / Available       | 503  |
| Missing/unavailable metadata schema or storage                   | Unhealthy; queue Unavailable         | 503  |
| Timeout or unexpected probe failure                              | Unhealthy; Unavailable / Unavailable | 503  |

Use `/health` for process restart probes and `/health/ready` when AI dependency readiness should gate routing or alerts.
An intentionally closed egress switch with AI enabled fails AI readiness without stopping deployment operations. Queue
checks still run while AI is disabled because automatic wakeup dispatch and retention remain active. The probe is
read-only and never resolves or invokes an LLM, checks remote credentials, claims work, loads diagnostics or changes
consent. Ready confirms local configuration and metadata reads only; provider reachability, trigger installation,
write permissions, worker progress, Loki/Mimir availability and deployment readiness are outside its scope. Protect
probe routing and cadence using the hosting network/reverse proxy; the finite probe response contains no tenant data.

Both profiles explicitly register the AutoMate.Analysis activity source and meter alongside existing deployment/security
sources. The shared export policy allows only fixed analysis operations/outcomes and dimensionless usage/wait/active
measurements. Registration does not enable AI or change provider/consent policy.

Web dependency composition, authentication, options, and HTTP pipeline configuration. It configures OpenTelemetry for
AutoMate's logs, traces, metrics, deployment diagnostic activity/meter sources, and security rate-limit rejection
events. Development console export is on by default; optional OTLP export is controlled by `OpenTelemetry` options and
has no committed credentials.

GitHub App credentials are required and validated at startup only in the SaaS hosting profile. Self-hosted pages can
resolve shared deployment services without configuring a GitHub App.

## Source inventory

- `AppConfiguration.cs`
- `AzureSubscriptionResolver.cs`
- `JwtPayloadReader.cs`
- `ServiceConfiguration.cs`

## Boundary

Keep presentation behavior here. Components, hubs, routes, and services use Application contracts; concrete
Infrastructure types are registered only in Web/Configs.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)

`TelemetryStorage` requires Loki/Mimir and validates endpoints, transport, managed onboarding and quotas
at startup. [Telemetry hosting](../../docs/deployment-telemetry.md) documents operator secrets and retention controls.

Telemetry startup validation uses `TelemetryStorageOptionsValidator` to identify individual invalid settings without
exposing their values. Local IDE launches can import the complete pilot settings with
`deploy/telemetry/Configure-TelemetryDevelopment.ps1`.

Both hosting profiles require LokiMimir/DiskGateway. Web supplies those defaults before binding operator settings and
rejects PostgresOutbox even when explicitly configured. Endpoint and gateway credentials remain mandatory. Legacy
PostgreSQL reads and outbox draining continue during migration. See ../../docs/saas-telemetry.md.

`DeploymentDiagnostics` validates a buffer capacity of 16–16,384 events, a persistence deadline of 1–60 seconds and a
delivery deadline of 1–30 seconds. Defaults are 512 events, 10 seconds and 2 seconds. The log hub has explicit 64 KiB
transport buffers in each direction; sinks honor cancellation without changing callback names or subscription policy.

SelfHosted registers one host-owned `LocalDeploymentLogStreamManager` singleton behind `ILocalDeploymentDiagnostics`
and as a hosted service. Scoped `DockerService` implements both Docker operation and diagnostic source ports. SaaS
continues to omit local deployment collectors and registers only its existing disabled Docker operation adapter.

The AI result boundary registers `IAnalysisResultValidator` as a singleton using the central singleton redactor.
Both the typed provider client and scoped analysis service/worker use it. Both profiles remain default-off for AI;
resolving these ports does not require provider credentials or initiate any provider request.

Both profiles register `DeploymentAnalysisRetentionService` independently of AI admission, using the existing singleton
`TimeProvider.System` and a fresh database scope per startup/hourly cleanup pass. Expiry guards and owner deletion use
the
same clock.

Both profiles register `DeploymentAnalysisRetentionService` independently of AI admission, using the existing singleton
`TimeProvider.System` and a fresh database scope per startup/hourly cleanup pass. Expiry guards and owner deletion use
the
same clock.

## Structured platform logging

Both profiles export log scopes and share service.name, service.version, deployment.environment and a bounded
`automate.hosting_profile` resource attribute. `SecurityAuditResultHandler` logs fixed challenge/denial outcomes and
then delegates to the default authorization handler, preserving responses and redirects. OAuth ticket preparation and
remote failure, local login and rate-limit rejection use the fixed Application `OperationalLog` event API. Credentials,
users, paths, headers, query strings and external failure bodies are not event properties. Startup database failure
logs retain the failure type and omit the raw exception. Web/Observability now sanitizes legacy/framework logging
through the registered ILoggerFactory and SDK processors.
Its explicit catalog and typed allowlists retain safe audit correlation and omit unapproved payloads. Real SDK
exports/resource attributes and default authorization behavior are tested
without a collector in `Web.Tests/OperationalLoggingTests.cs` and hosting-profile tests.

Tracing includes Microsoft.AspNetCore.SignalR.Server. SQL command and DeploymentTelemetry HTTP Information chatter is
suppressed by tracked category defaults; warnings/errors and application Information remain visible. Temporarily set
those Logging:LogLevel categories to Information for additional safe metadata; URLs and SQL text remain omitted. Console
export stays opt-in. Filters do
not disable tracing or Loki/Mimir ingestion.

M5 metric views omit request-derived HTTP dimensions and unknown instruments, retain finite operational/runtime labels,
and disable exemplars. Default resource detectors are replaced by the four-field SafeResourceDetector shared by logs,
traces and metrics; arbitrary OTEL_RESOURCE_ATTRIBUTES are excluded. See ../Observability/README.md for approved labels
and configuration fallback behavior.

Both profiles resolve scoped IDeploymentAnalysisContextBuilder using existing metadata, diagnostic-history and Mimir
ports. AiAnalysis options add MaximumContextBytes (48,000) and MaximumContextTokens (12,000 conservative units),
alongside
MaximumContextCharacters (24,000). These limit encoded diagnostic input; they do not count whole-request/schema/output
cost. Context acquisition does not invoke an LLM; AI remains default-off and provider/tenant/region egress remains gated
by implemented operator/consent gates and unresolved real provider approvals in PLAN.md.

AI egress options bind with startup validation when ProviderEgressEnabled is requested. Shared runtime policy uses
IOptionsMonitor and a scoped fresh-metadata IAnalysisEgressAuthorizer in both profiles. The initial OpenAI typed client
has redirects and automatic HTTP retries disabled, so a retry cannot reuse an earlier consent decision. Defaults and
operator onboarding are documented in Infrastructure/Ai/README.md; enabling AI is not a provider/region approval.

The [staged rollout procedure](../../docs/ai-analysis-operations.md#staged-feature-enablement-and-shutdown) uses the
approved owner cohort and independent feature/automatic/egress flags. Standard appsettings JSON reload propagates to
IOptionsMonitor and cancels active OpenAI and Azure OpenAI requests; effective environment/command-line overrides
require restart.
Verify shutdown on every replica. This is a local cancellation control, not remote recall or a cluster barrier.

AI startup options independently validate LeaseDurationSeconds (30–900) and MaximumRecoveryAttempts (1–10), even
while AI remains disabled. These configure renewable ownership and bounded interruption recovery, without enabling
provider egress or automatic HTTP retries.

Retry options are validated even when AI is disabled: MaximumProviderRetries 0–5, RetryBaseDelaySeconds 1–300,
RetryMaximumDelaySeconds 5–3,600, with base no greater than maximum. Defaults are 2/10/300. They do not enable egress.

DailyProjectLimit startup validation accepts 0–1,000, including disabled AI configurations. Zero blocks new analyses
without preventing owner reads/cancellation/deletion; stable replay/active coalescing still require admission policy.
Project allowance is serialized and receipt-backed; it is distinct from later tenant/cost limits.

AiAnalysis:MaximumConcurrency validates 1–16 even with AI disabled, defaulting to one processing slot per instance.
Set AiAnalysis__MaximumConcurrency in environment configuration and restart to resize the worker. Invalid settings
fail options validation; there is no silent clamp. Other AI policy/consent monitor checks continue to refresh normally.

Provider composition uses AnalysisProviderCatalog and the scoped ConfiguredAnalysisProvider port. OpenAI's concrete
HttpClient retains no-redirect behavior. Add an Infrastructure adapter plus its DI client and
AnalysisProviderRegistration (exact approved route) to extend selection; no Application workflow/UI change is needed.
Enabled egress validates the
selected catalog entry and common policy at startup; unknown/unapproved provider routes fail closed. Missing API keys
remain unavailable at adapter invocation and never appear in diagnostics or configuration validation output.

M8 adds independent startup validation for AiAnalysis:TimeoutSeconds (5–300), MaximumOutputTokens (1–8,192),
MaximumContextCharacters/Bytes/Tokens (each 1–131,072) and ResultRetentionDays (1–90). These apply even with AI
disabled;
defaults remain 60 seconds, 8,192 output tokens, 24,000/48,000/12,000 context limits and 90-day result retention.
No provider credentials, egress or automatic analysis are enabled by validation. Exact provider/region/tenant/category
approvals remain mandatory only when provider egress is requested; missing keys remain a safe runtime unavailable state.

OpenTelemetryOptionsValidator runs at startup. OtlpEndpoint is optional; if supplied, it must be an absolute HTTP (S)
collector URL up to 2,048 characters, without embedded credentials, whitespace, query or fragment. Authentication
belongs in protected exporter configuration, never the URL or tracked JSON. HTTP remains supported for trusted internal
collector networks; operators own TLS/network policy. ServiceName must have 1–100 nonblank characters; optional
Environment has the same bound. SafeResourceDetector still sanitizes labels. Invalid endpoints are never registered
with an exporter and fail startup using fixed messages that omit supplied values. Valid endpoints retain existing
export behavior; changing OTLP export/resource configuration requires restart. The standard configuration binder still
handles type conversion; validators handle typed settings and policy bounds.

Both profiles register `azure-openai` as a separate typed HttpClient behind the existing catalog, with redirects
disabled
and factory HTTP loggers removed. Exact Azure resource routing and protected key presence use `AzureOpenAiOptions`;
unknown providers and mismatched routes remain unavailable. No SDK, migration or Application contract change is needed.
See [Azure setup](../../docs/azure-openai-setup.md) for the complete manual pilot configuration.
