# Shared platform logging safety

Operational logging preserves reviewed source module categories and readable scope GUIDs (request, actor, project,
deployment and analysis IDs). UserId is a typed internal GUID for security audits, never an email, name or external
account identifier. Outbound HTTP start/send/response/completion summaries retain finite methods, numeric status and
duration while excluding request URLs/bodies/headers. Cookie authentication summaries retain a finite scheme and
sign-in/sign-out/challenge/forbid outcome. Actual HttpClientFactory tests verify runtime fields, including typed HttpMethod.
Startup, migration and authentication framework categories retain their source names and normal category filters.
Reviewed modules' unfamiliar events retain numeric event codes and safe exception classes; unknown categories still
fail closed. Additional templates must be reviewed; raw formatters, SQL and exception bodies remain blocked.

Reviewed hosting lifecycle templates become fixed start/listener/environment/root/shutdown summaries without values.
Compose exit codes, finite command outcomes and queued-job completion stay readable; host paths, addresses, customer
names and exception bodies remain excluded. Unknown events still fail closed and must be reviewed individually.

Dashboard is an approved category for its fixed subscriber-failure template. DockerContainerNotFoundException is an
approved failure type; provider response bodies remain excluded. The analysis.outcome trace field accepts only the
reviewed completed/failed/canceled/skipped/empty/discarded/retry_scheduled values.

This module owns SDK-independent platform log policy for both Web hosting profiles and the private Telemetry process.
PlatformTelemetryPolicy, PlatformLogCatalog and SafeLoggerFactory moved here from Web/Observability so Infrastructure
and Telemetry do not reference Web. No new packages or host dependencies were introduced.

AddSafePlatformLogging wraps the existing host ILoggerFactory once. Every registered provider receives detached safe
state/scopes, reviewed literal templates/categories and bounded approved properties, without raw exception objects,
unknown event/category names, arbitrary formatters or external URL/SQL/body text. It retains filters, levels, numeric
event IDs and allowed correlation; activity tracking includes only trace/span/parent IDs. Reviewed spool/delivery/daily
worker templates retain fixed alerts and finite failure classes. Do not replace the factory after registration or use a
separate raw LoggerFactory for application logs. Provider/enrichment hooks bypassing this factory require review.

Web/Observability retains SDK-specific log/trace processors, metric views and resource detectors; these recheck exports.
The private Telemetry host has no OpenTelemetry exporter registration; its ordinary providers cross the shared factory.
Any future private-host SDK exporters require the same processor/view/resource checks before enablement.

Terminal text is not platform log state. It goes through IDiagnosticRedactor and the bounded disk gateway, Loki/Mimir
adapters, history and browser boundaries instead. Operational authentication/configuration inputs retain their required
credentials and are not exported as platform telemetry or diagnostic context.

Web.Tests/PlatformTelemetrySafetyTests covers the shared factory and real SDK/wire exports. Hosting-profile tests cover
Web composition. TelemetryHostSafetyTests builds the production private-host composition over loopback and verifies
401/400 responses plus ordinary-provider output without external stores or running disk/background workers.

DeploymentAnalysisService is an approved platform category for fixed owner cancellation audit events. It uses the
existing operation/outcome template, finite CanceledByOwner value and GUID-only correlation; no owner identity or text
payload is emitted.

FailedDeploymentAnalysisDispatcher is an approved platform category. Its fixed admission-deferred warning records only
the exception type; no provider response, diagnostic payload or context is emitted.
