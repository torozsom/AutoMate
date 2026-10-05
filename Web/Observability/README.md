# Platform telemetry safety

Web/Configs/OpenTelemetryOptionsValidator startup-validates optional collector URLs and bounded resource labels without
echoing configured values. Malformed/non-HTTP (S) endpoints or embedded credentials/query/fragment are rejected before
exporters are configured, rather than silently disabling export. Approved existing URLs and default-off console export
retain their behavior. Collector authentication belongs in protected exporter configuration, not URL parameters or JSON.

## Operator logging

Ordinary ILogger output goes to console/debug providers; AutoMate currently has no application file-log provider.
Console output includes UTC timestamps to distinguish queue wait, command execution and source cleanup durations.
Console records are single-line and include readable safe scopes and the originating logger module.
RequestAuditMiddleware
records all requests reaching routed application middleware, including authentication callbacks,
authorization/rate-limit/
antiforgery rejections and endpoint failures. It retains a generated request GUID, finite route area, method, status,
duration, authentication state and internal user GUID when available. Paths, queries, bodies, cookies and credentials
are excluded. Requests handled before routing (such as HTTPS redirects and Swagger middleware) do not cross this audit.
Long-lived SignalR requests finish when the connection ends; internal UI operations use service/queue audit logs.
Framework authentication is Information; general ASP.NET chatter remains Warning. Deployment queues log acceptance;
orchestrators log persisted status changes and completion. Security events cover login starts/outcomes, logout,
registration, verification, account connections and diagnostic-consent changes. Internal user GUIDs intentionally
support security correlation; names, emails, external account IDs and tokens remain excluded.
`.logs/output.txt` is an operator-captured console transcript, not an automatic sink. Docker-hosted AutoMate stdout
retention depends on the container logging driver. Deployment terminal history follows a separate redacted durable
disk-gateway/Loki pipeline; numeric metrics use Mimir (30-day detail and 365-day daily summaries by default).

OpenTelemetry instrumentation is already registered. `OpenTelemetry:ExportConsole` defaults to false and
`OpenTelemetry:OtlpEndpoint` is optional. For durable platform logs, spans and metrics, configure an approved OTLP
Collector and persistent backends; enabling a console exporter alone does not save history. The existing deployment
Loki/Mimir gateway is not an OTLP Collector or a trace backend. Keep production retention/access controls explicit.
Never save a parallel unredacted log copy. Required operational credentials belong in protected configuration/storage,
not logs. Improve diagnostics with reviewed event names, GUIDs, statuses and failure classes instead of raw payloads.

`AutoMate.Analysis` is registered for both hosting profiles. SafeTraceProcessor retains only its fixed workflow span
names and finite outcomes; SafeMetricPolicy approves six analysis instruments, retaining operation/outcome dimensions
only for operation counts/durations. GUIDs remain trace correlation, never metric labels. Usage and active/queue-wait
instruments have no dimensions. Actual SDK/wire tests verify payload omission and approved export.

The Web composition root registers this policy for both hosting profiles. Application remains independent of the
OpenTelemetry SDK. Deployment terminal text continues through its shared redactor and Loki/Mimir disk gateway.

`SafeLoggerFactory` sanitizes state before registered console, debug and OpenTelemetry providers receive it. Scopes are
snapshotted when opened; subsequent caller mutations cannot change exported values. Caller formatters, exception
objects,
event names, arbitrary scope objects and unknown properties are omitted. Numeric event IDs, levels, GUID correlation and
approved finite audit outcomes survive. Activity scope capture includes trace/span/parent IDs only.

`PlatformLogCatalog` lists reviewed literal templates and categories. `PlatformTelemetryPolicy` independently allows
typed GUID/numeric fields and finite operations, outcomes, sources, statuses and failure classes. Unknown templates
become
`Platform event; unapproved details omitted.`; unapproved placeholders become `[REDACTED]`. Unknown category names
become
`AutoMate.Platform`. Review additions explicitly; do not automatically import external templates or attributes. Known
category filters remain effective; unfamiliar categories use the fallback's filter. Verbose HTTP/SQL settings expose
safe metadata rather than request URLs or SQL text.

`SafeLogProcessor` rechecks SDK bodies/attributes before console/OTLP exporters and removes exception and trace-state
payloads. Scopes rely on the upstream factory because the SDK exposes no supported scope-replacement API. Custom
providers
must use the registered factory; bypassing it or adding enrichment after the safety processors bypasses this boundary.

`SafeTraceProcessor` retains trace identity, safe tags and status codes, normalizes display names, and removes baggage,
trace state and status descriptions before export. Spans containing events/links are omitted because those collections
cannot be safely replaced through supported SDK APIs. Spans exceeding 128 tags are also omitted. Both simple and batch
exporters honor this suppression; `automate.platform.spans.omitted` counts it using finite reasons only. Omitting such
spans reduces trace detail and may leave gaps in otherwise retained trace relationships.

`SafeMetricPolicy` installs views before aggregation/export. Approved AutoMate instruments retain only the finite
labels used by their reviewed emitters (lane, diagnostic taxonomy/sink, authentication state, query/write and omission
reason). Invalid diagnostic enums map to Unknown. Framework HTTP measurements retain aggregate counts/durations without
request URLs, hosts, routes, error classes or arbitrary enrichment. Runtime metrics retain finite heap generations and
CPU modes; exception classes are omitted. Unknown instrument identities are dropped. Metric streams have bounded
cardinality (4,096 application points; 16 framework points). Exemplars are explicitly disabled because removed metric
attributes can otherwise survive inside exemplars. This policy filters dimensions rather than rewriting measurement
values; review both instrument identity and emitter value domains when adding metrics.

`SafeResourceDetector` replaces default detectors with exactly service.name, service.version, deployment.environment
and automate.hosting_profile. Operator labels are limited to 64 ASCII letters/digits/dots/dashes/underscores and checked
with the shared redactor. Invalid labels use fixed defaults. Arbitrary OTEL_RESOURCE_ATTRIBUTES, automatic host/process
metadata and environment-provided service names are not imported. The same resource is used for logs, traces and metrics
in both profiles. Extra operational identity must be approved separately rather than passed through environment
detectors.

The diagnostic queue/cache audit is recorded in Infrastructure/Diagnostics/README.md. General deployment inputs and
repository metadata remain usable by their operational consumers; they are not diagnostic/AI context caches.
M5 now supplies bounded in-memory context selection and exact evidence-reference membership through Application/Ai
and Infrastructure/Ai. Final egress/host-boundary and tenant/region approval remain separate work. AI remains disabled
by default. No PostgreSQL diagnostic payload writer is introduced.

`Web.Tests/PlatformTelemetrySafetyTests.cs` covers ordinary providers, real SDK log exports, simple/batch trace
suppression
and actual OTLP protobuf over a loopback collector. Hosting-profile tests verify the registered factory and resources.
SDK extension contracts are documented in the
[OpenTelemetry logging SDK guide](https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/docs/logs/extending-the-sdk/README.md).

MetricResourceSafetyTests verifies actual OTLP protobuf, dropped attributes/unknown instruments, disabled exemplars and
bounded resource labels. Hosting-profile tests verify identical four-field resources across all three SDK providers.
The [SDK metric view/exemplar guide](https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/docs/metrics/customizing-the-sdk/README.md)
and [.NET runtime metric definitions](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/built-in-metrics-runtime)
describe the underlying export and finite runtime dimension contracts.
