# Host configuration and pipeline

Concrete service composition, profiles, OAuth callbacks, startup initialization and liveness/readiness mapping.

## Contents

| File                                                 | Types or asset                                         | Responsibility                                                                                                      |
|------------------------------------------------------|--------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------|
| [AnalysisHealthChecks.cs](AnalysisHealthChecks.cs)   | `AnalysisReadinessHealthCheck`, `AnalysisHealthChecks` | Maps safe Application readiness to host health without provider requests or exception disclosure.                   |
| [AppConfiguration.cs](AppConfiguration.cs)           | `AppConfiguration`                                     | Ordered middleware, endpoint/hub/probe mapping and startup metadata migrations.                                     |
| [HostingProfileOptions.cs](HostingProfileOptions.cs) | `HostingProfileOptions`                                | Selects which deployment capabilities are exposed by this AutoMate installation.                                    |
| [OAuthAccountCallbacks.cs](OAuthAccountCallbacks.cs) | `OAuthAccountCallbacks`                                | Links provider account tickets while preserving AutoMate's existing authentication cookie and token exchange order. |
| [ServiceConfiguration.cs](ServiceConfiguration.cs)   | `ServiceConfiguration`                                 | Composition, validated options, authentication, provider clients, host workers and telemetry export.                |

## Usage and integration

ServiceConfiguration.AddApplicationServices binds/validates options, authentication/authorization, scoped ports, hosted
workers, provider HTTP clients and safe telemetry. HostingProfileOptions maps SelfHosted to local/cloud and SaaS to
cloud-only. OAuthAccountCallbacks owns ticket/account linking, ARM exchange and cookie-preserving completion.
AppConfiguration orders middleware and maps probes, hub and discovered endpoints.

## Contracts and lifecycle

Pipeline order covers forwarded headers/environment diagnostics, HTTPS/routing, request audit, authentication, rate
limiting, authorization, antiforgery, then hub/probes/endpoints. Preserve ordering and static-before-Razor endpoint
discovery. InitializeInfrastructureAsync applies checked-in metadata migrations in a scope; its catch/log behavior does
not prove database initialization succeeded.

Preserve original aliases/lifetimes/DbContext pooling; independent component operations create fresh scopes. SaaS alone
requires GitHub App credentials. Both profiles register the disk telemetry gateway and reject new PostgreSQL payload
modes. Local builds/stops are uncapped by default; concurrency and AI/telemetry bounds validate at startup even when AI
is disabled.

AzureRuntime and AzureLiveLogs use separate nonredirecting clients; live reads separate header timeout from canceled
body lifetime. OpenAI/Azure adapters have exact approved routes, separate credentials and no factory HTTP payload
logging. OAuth uses explicit connected-account identity and scopes; a prepared ticket is not final cookie sign-in.

## Probe behavior

| Condition                                                             | Readiness HTTP          |
|-----------------------------------------------------------------------|-------------------------|
| AI disabled and queue metadata readable                               | 200; Disabled/Available |
| Approved route/credential configuration and metadata readable         | 200; Ready/Available    |
| Enabled but egress disabled or approval/credentials/spend unavailable | 503; Degraded           |
| Invalid policy, unavailable metadata/schema or probe failure/timeout  | 503; Unhealthy          |

The five-second AI probe returns only status/configuration/queue and allows anonymous requests. Liveness excludes it.
Neither probe invokes an LLM, grants consent, checks remote credentials, verifies write permissions/triggers/worker
progress, nor establishes deployment/backend availability. Use the appropriate probe for restart versus AI readiness
gating.

## Related documentation

- [Parent module](../README.md)
- [Composition index](../README.md)
- [Scheduler defaults](../../../Application/Orchestration/Jobs/README.md)
- [Provider/worker semantics](../../../Infrastructure/Ai/README.md)
- [Registration tests](../../../Test/Web/Hosting/HostingProfileRegistrationTests.cs)
- [HTTP probe tests](../../../Test/Web/Analysis/AnalysisReadinessTests.cs)
- [Deployment configuration](../../../deploy/README.md)

Observability registration delegates to the shared Infrastructure SDK module. Reviewed application HTTP clients attach
OperationalHttpHandler; exporter HTTP clients never do. DeploymentDiagnostics validates the live interval separately
from history sampling and sink deadlines.
