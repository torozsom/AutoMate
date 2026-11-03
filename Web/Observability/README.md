# Web export safety

OpenTelemetry SDK processors, finite metric/resource policy and safe HTTP operation audits.

## Contents

| File                                                                                                | Types or asset           | Responsibility                                                                                        |
|-----------------------------------------------------------------------------------------------------|--------------------------|-------------------------------------------------------------------------------------------------------|
| [RequestAuditMiddleware.cs](RequestAuditMiddleware.cs)                                              | `RequestAuditMiddleware` | Audits HTTP operation boundaries without recording URLs, queries, headers, cookies or request bodies. |
| [SafeLogProcessor.cs](../../Infrastructure/Observability/OpenTelemetry/SafeLogProcessor.cs)         | `SafeLogProcessor`       | Rechecks SDK attributes/body before exporters; scopes are snapshotted by SafeLoggerFactory.           |
| [SafeMetricPolicy.cs](../../Infrastructure/Observability/OpenTelemetry/SafeMetricPolicy.cs)         | `SafeMetricPolicy`       | Approves metric identities and removes request/provider-derived dimensions before aggregation.        |
| [SafeResourceDetector.cs](../../Infrastructure/Observability/OpenTelemetry/SafeResourceDetector.cs) | `SafeResourceDetector`   | Builds only reviewed resource fields; environment detectors cannot import arbitrary attributes.       |
| [SafeTraceProcessor.cs](../../Infrastructure/Observability/OpenTelemetry/SafeTraceProcessor.cs)     | `SafeTraceProcessor`     | Sanitizes before simple/batch exporters; spans with immutable untrusted events/links fail closed.     |

## Usage and integration

SafeLogProcessor rechecks SDK state after Infrastructure SafeLoggerFactory sanitization. SafeTraceProcessor sanitizes
activities before export and omits immutable untrusted events/links. SafeMetricPolicy controls identities/dimensions
before aggregation. SafeResourceDetector supplies reviewed resource labels. RequestAuditMiddleware logs finite HTTP
operation areas/outcomes. Request methods are mapped to fixed standard labels (or `OTHER`) before logging;
both boundary events use the method captured when the request started.

## Contracts and lifecycle

Do not export request URLs/query/headers/cookies/bodies, prompts/context/results, raw exceptions or arbitrary provider
strings. Internal GUID correlation belongs in approved logs/traces, not metric dimensions. Unknown
properties/categories/templates fail closed through shared policy; adding instrumentation requires coordinated allowlist
review. Request audit observes operation boundaries, not successful user authorization by itself.

## Operator logging

Infrastructure owns SafeLoggerFactory, literal catalogs and typed field policy; preserve that boundary before all
ordinary providers. Shared SDK processors add a second export check in both hosts.

## Redacted console exception snapshots

The shared bounded console sink emits separate operator records and never puts exception prose into ordinary
ILogger/export state.
See [console diagnostics](../../Infrastructure/Observability/README.md#console-exception-diagnostics).

## Related documentation

- [Parent module](../README.md)
- [Shared pre-provider policy](../../Infrastructure/Observability/README.md)
- [Instrumentation vocabulary](../../Application/Diagnostics/README.md)
- [Exporter options](../Configs/Observability/README.md)
- [Export tests](../../Test/Web/Hosting/PlatformTelemetrySafetyTests.cs)
- [Metric/resource tests](../../Test/Web/Hosting/MetricResourceSafetyTests.cs)
- [HTTP audit tests](../../Test/Web/Diagnostics/RequestAuditTests.cs)

## Shared SDK ownership

SDK processors, resource policy and views now live
in [Infrastructure SDK integration](../../Infrastructure/Observability/OpenTelemetry/README.md).
This module retains RequestAuditMiddleware. Web adds UI/security sources to the shared safe registration.
