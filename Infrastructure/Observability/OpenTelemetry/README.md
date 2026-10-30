# Safe OpenTelemetry SDK integration

Shared Web/private-Telemetry SDK configuration. Application instrumentation remains SDK-independent.

| File                                                       | Responsibility                                                                                       |
|------------------------------------------------------------|------------------------------------------------------------------------------------------------------|
| SafeOpenTelemetryRegistration.cs                           | Idempotent host registration, named bounded exporters, shared resource configuration.                |
| OpenTelemetryOptions.cs / OpenTelemetryOptionsValidator.cs | Compatible exporter settings plus validated volume controls.                                         |
| SafeLogProcessor.cs                                        | Sanitizes SDK log state before every exporter.                                                       |
| SafeTraceProcessor.cs                                      | Sanitizes spans and omits immutable unsafe events/links.                                             |
| OperationalTraceFilter.cs                                  | Deterministically filters only fast successful sends and empty claims after sanitization.            |
| SafeMetricPolicy.cs                                        | Reviewed identities/dimensions, bounded series, explicit millisecond buckets and disabled exemplars. |
| SafeResourceDetector.cs                                    | Four reviewed host labels plus one random process instance identity.                                 |

## Registration and configuration

Both hosts call AddSafeOpenTelemetry once. Web includes UI/security sources; the private host defaults to
AutoMate.Telemetry and shares ingestion/storage/runtime instrumentation. Web defaults to AutoMate. One randomly
generated service.instance.id is stable across every signal in the process and changes after restart.

OpenTelemetry:OtlpEndpoint takes precedence over OTEL_EXPORTER_OTLP_ENDPOINT. Without either, OTLP export is disabled.
Standard environment protocol/header/timeout settings are resolved by the SDK. A custom endpoint is a collector base
URL: HTTP/protobuf appends /v1/logs, /v1/traces and /v1/metrics; gRPC uses the base endpoint. Never put credentials in
URLs.
The SDK suppresses exporter HTTP instrumentation; application observation handlers are never attached to exporter
clients.

Log/trace batches hold at most 2,048 items and export at most 512 per batch with a five-second processor export timeout.
Export pressure may drop platform telemetry; it cannot backpressure durable deployment ingestion. SDK metric cardinality
bounds apply before export. A durable external receiver and its retention/alert routing remain operator
responsibilities.

| OpenTelemetry setting    | Default | Constraint |
|--------------------------|--------:|------------|
| SignalRSuccessSampleRate |    0.01 | finite 0–1 |
| EmptyClaimSampleRate     |    0.01 | finite 0–1 |
| SignalRSlowMilliseconds  |     100 | positive   |
| ClaimSlowMilliseconds    |     250 | positive   |

Filtering uses deterministic FNV-1a over span identity after sanitization. It filters individual low-level spans, not
whole traces. Slow, failed, canceled, timed-out, unknown-outcome and unrelated operations retain their prior export
behavior. Privacy omissions always take precedence. automate.platform.spans.filtered counts volume filtering;
automate.platform.spans.omitted counts safety omissions. Every SignalR send and analysis claim still updates metrics.

## Verification

OperationalTelemetryTests exercises simple/batch export ordering, deterministic selection, actual loopback OTLP
resources for all signals, safe rate-limited HTTP warnings and exporter-pressure isolation from real temporary spool
receipts. Existing platform/metric-resource/hosting tests retain adversarial wire-safety and option validation coverage.
See the [operator runbook](../../../docs/platform-observability.md) and [parent policy](../README.md).
