# Web Tests

DeploymentAnalysisPanelTests covers remote projects without configuration: consent eligibility remains tied to the
current deployment and explicit saving reads the created configuration back through the owner-authorized port.

HostingProfileRegistrationTests verifies shared budget guard registration in both profiles and rejects unsafe tenant,
rate, shared concurrency, currency and monetary precision/bounds even with AI disabled, without revealing configured
private values. Defaults leave spending disabled and use USD.

AnalysisReadinessTests hosts production health middleware over loopback and verifies healthy disabled AI, policy and
configuration degradation, unavailable queues, cancellation/timeouts, fixed finite JSON and liveness isolation. Both
hosting profiles verify the scoped Application readiness port and five-second tagged health-check registration.

HostingProfileRegistrationTests exercises real M8 startup validation for AI timeout/context/output/retention bounds,
exact boundary acceptance while disabled, malformed/credential-bearing OTLP URLs and fixed failure messages. Resource
labels are bounded without echoing invalid values. Defaults are verified in both hosting profiles. No hosted workers,
collector connections or provider requests are started by these validation tests.

DashboardStatusTests attaches the real status implementation to a renderer dispatcher and delivers background terminal
notifications; it verifies model/control updates, render commits and ignored callbacks after disposal. Analysis SDK
export coverage in DeploymentTracingTests and MetricResourceSafetyTests checks fixed span names/parentage, GUID
correlation, finite metric dimensions and omitted injected payloads over actual OTLP serialization.

Telemetry presentation tests cover resource formatting, weighted daily averages, sparse markers, gaps, independent
log/metric controls, stale replies and access denial. Static-rendering tests produce provider-fixture previews under
`.artifacts/metrics-preview` using the actual summary/history components; these are not live deployment data.
Run client interaction checks with `node --test Web.Tests/JavaScript/telemetry-chart.test.cjs`.

Credential-free tests of the Web composition root and hosting-profile configuration. Tests resolve the dashboard's
shared cloud dependencies without starting hosted workers or contacting PostgreSQL, Redis, GitHub, or Azure.

Run with `dotnet test Web.Tests/Web.Tests.csproj`.

LogHub cancellation tests distinguish navigation disconnects from provider failures and retain token validation.

Deployment-history tests verify saved terminal output survives metric-backend failure and empty output has an explicit
notice. Metric display tests check restored numeric units. Browser-wrapper regression tests exercise resize feedback,
hidden terminals, history replacement, and disposal using Node's built-in test runner:

`node --test Web.Tests/JavaScript/xterm-wrapper.test.cjs`

ProjectTelemetrySummaryTests verifies overlapping analytics loads use distinct scoped services and dispose both scopes,
preventing shared circuit-context concurrency failures.

`DeploymentTerminalPresentationTests` verifies the same visible stderr marker for live/history rendering while retaining
legacy/stdout text compatibility.

`DeploymentAnalysisEndpointTests` hosts the real DELETE route on an ephemeral loopback Kestrel port with synthetic
principals and application ports. It verifies authentication, real antiforgery validation, principal-to-owner resolution
and 204/404/409 responses. Hosting-profile tests verify cleanup registration even with AI disabled.

`DeploymentAnalysisEndpointTests` hosts the real DELETE route on an ephemeral loopback Kestrel port with synthetic
principals and application ports. It verifies authentication, real antiforgery validation, principal-to-owner resolution
and 204/404/409 responses. Hosting-profile tests verify cleanup registration even with AI disabled.

`OperationalLoggingTests` captures actual OpenTelemetry SDK export records, including GUID scopes and trace correlation.
It checks authorization challenge/forbid preservation, omission of injected storage exceptions/diagnostic payloads and
safe collector rejection. Hosting-profile tests inspect actual service/environment/version/profile Resource attributes.

Real form-login tests also verify successful cookie sign-in and denied-login redirects while audit properties omit
credentials, usernames and returned authentication error text.

DeploymentTracingTests checks actual SDK span exports, exception identity, parentage and payload omission. Hub/send
tests
verify disconnect/provider cancellation and deadline distinctions. Both profiles register the .NET SignalR source.
The rendering assertion permits insignificant whitespace while checking the same visible text.

OperationalLoggingTests additionally exports real GitHub cache/network and status-subscriber failures, checking privacy
while retaining API fallback, cancellation and notification isolation. DeploymentFailurePrivacyTests exercises the
actual dashboard scanner-failure path and verifies safe guidance without opening configuration.

RealTimeLogStreamerTests also injects sensitive terminal/notice/metric text at the final transport boundary. Captured
actual client-proxy arguments are masked while project routing, event/cursor identity and cancellation behavior remain
unchanged. Both hosting-profile DI checks resolve the required shared redactor.

PlatformTelemetrySafetyTests verifies detached safe scopes before ordinary providers and actual SDK exports, suppression
of event/link payload spans with simple and batch exporters, and sanitized real OTLP protobuf via a loopback collector.
Hosting-profile tests verify unknown messages and sensitive scope values are omitted by the registered composition root.

MetricResourceSafetyTests verifies metric filtering through real OTLP protobuf and bounded resource labels. Both
hosting profiles verify logs, traces and metrics share exactly the same four approved resource attributes.

HostingProfileRegistrationTests also rejects invalid AI retry bounds even with provider egress disabled; no hosted
worker or external provider is started by these option-validation fixtures.

DeploymentAnalysisEndpointTests exercises DELETE and POST cancellation through real loopback middleware, with
authentication, actual antiforgery cookie/token validation, resolved owner checks and fixed use-case HTTP mappings.
Application ports are synthetic; no provider or production database is contacted.

HostingProfileRegistrationTests validates DailyProjectLimit bounds and accepts zero without enabling AI or starting
external services.

DeploymentAnalysisPanelTests renders all durable states, verifies encoded text, omitted partial/stale results, action
gates and live status semantics. It writes synthetic provider fixtures under .artifacts/analysis-preview using actual
Bootstrap/application/component CSS; these do not load project data or call providers. Browser checks cover light/dark
contrast and 375px layout with 200% text. The fixture is a passive static render, not an authenticated live deployment.

DeploymentAnalysisPollingTests exercises the actual ProjectDetails scoped read and dispatcher tick methods: overlapping
reads, owner/deployment changes, disposal cancellation, transient failure recovery, busy suppression and stale failure
feedback. Synthetic Application ports keep these checks independent of databases, timers and provider traffic.

DeploymentAnalysisActionTests invokes real consent/cancel handlers to check exact project selection, preserved consent
on rejection/uncertainty, owner/deployment/analysis arguments, cancellation readback and completion races, same-identity
retry, duplicate suppression and stale-action fencing. Panel tests cover cancel eligibility independently of AI/consent
enablement, authored skip guidance and linked egress disclosure. Browser DOM checks use synthetic previews for 375px
layout at 200% text and light/dark control contrast; they perform no real consent changes or cancellation.

Azure provider registration tests resolve the real typed adapter and catalog in both hosting profiles without starting
workers or calling Azure. They verify separate Azure credential presence, exact resource mismatch rejection and retained
default-off startup. Windows Web host tests require Event Log/data-protection access outside a restricted sandbox.

ConsoleExceptionDiagnosticsTests exercises bounded UTF-8 snapshots, credential masking, nested exception/stack/status
details, approved correlation, payload/path suppression, logging filters and fail-safe writers/getters.
PlatformTelemetrySafetyTests verifies ordinary and real SDK exports remain metadata-only with the console sink active.
Both hosting-profile composition fixtures resolve the sink and shared safe logger factory.

## Deployment history update (2026-10-08)

Section navigation and shared history presentation remain provider-free. Fixture HTML supports browser review without
OAuth. Production external-stack tests retain their explicit opt-in requirements.

The provider-free Project Details and Deployment Details previews are also checked in Edge with
Web.Tests/Browser/project-sections.test.cjs. Set AUTOMATE_PLAYWRIGHT_MODULE to the installed playwright module and run
node Web.Tests/Browser/project-sections.test.cjs after TelemetryPageRenderingTests creates .artifacts/metrics-preview.
These browser checks cover fragments, focus, history navigation, mobile overflow and both themes; they do not require
OAuth or make provider calls.

ProjectDetailsMetricRecoveryTests covers reconnect callbacks after disposal and delayed metric replies or disposed
providers after the page closes, without renderer/provider access after cancellation.
