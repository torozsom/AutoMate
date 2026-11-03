# Shared platform logging policy

SDK-independent pre-provider log sanitization and separate bounded operator-console exception diagnostics.

## Contents

| File                                                               | Types or asset                                         | Responsibility                                                                                         |
|--------------------------------------------------------------------|--------------------------------------------------------|--------------------------------------------------------------------------------------------------------|
| [ConsoleExceptionDiagnostics.cs](ConsoleExceptionDiagnostics.cs)   | `ConsoleExceptionDiagnostics`, `ConsoleDiagnosticText` | Writes bounded, redacted exception snapshots only to the operator console, never to ILogger providers. |
| [PlatformLogCatalog.cs](PlatformLogCatalog.cs)                     | `PlatformLogCatalog`                                   | Reviewed source literals; new templates/categories require explicit review before platform export.     |
| [PlatformLoggingConfiguration.cs](PlatformLoggingConfiguration.cs) | `PlatformLoggingConfiguration`, `RegistrationMarker`   | Installs the shared pre-provider logging boundary in Web and private Telemetry hosts.                  |
| [PlatformTelemetryPolicy.cs](PlatformTelemetryPolicy.cs)           | `PlatformTelemetryPolicy`, `SafePlatformLog`           | Allowlisted platform state/scope sanitization and detached safe log projections.                       |
| [SafeLoggerFactory.cs](SafeLoggerFactory.cs)                       | `SafeLoggerFactory`, `SafeLogger`, `Scope`             | Sanitizes before any registered ILogger provider, including standard console/debug and OpenTelemetry.  |

## Usage and integration

PlatformLoggingConfiguration installs SafeLoggerFactory once in Web and private Telemetry. PlatformLogCatalog holds
reviewed categories/templates; PlatformTelemetryPolicy creates detached approved state/scopes.
ConsoleExceptionDiagnostics receives exceptions separately for bounded redacted stderr snapshots.
the [SDK submodule](OpenTelemetry/README.md) owns shared exporter processors/views for both hosts.

## Contracts and lifecycle

Ordinary providers receive reviewed literals, finite fields, safe internal GUID correlation and failure classes, without
arbitrary formatter output, SQL/URLs/provider prose or raw exception objects. Preserve category filters, levels and
event IDs; adding a template/category requires policy review. Do not replace the registered factory or introduce a raw
bypass.

Terminal payloads use the separate diagnostic redaction/storage path. Both hosts use the shared SDK policy before any
configured OTLP exporter. These boundaries do not claim masking can identify all arbitrary
sensitive prose.

## Console exception diagnostics

The console-only sink emits separate correlated records, never exception snapshots through ILogger state/export. It caps
UTF-8 records at 16 KiB, bounds exception traversal/stack symbols, omits source paths/arguments/Data/body dumps and
suppresses sensitive SDK messages. Central pattern/configured-value masking applies. Console access/retention remains
distinct from ordinary metadata-only platform exports; console failures are isolated.

## Related documentation

- [Parent module](../README.md)
- [Instrumentation vocabulary](../../Application/Diagnostics/README.md)
- [SDK export boundary](../../Web/Observability/README.md)
- [Shared export tests](../../Test/Web/Hosting/PlatformTelemetrySafetyTests.cs)
- [Private-host tests](../../Test/Web/Hosting/TelemetryHostSafetyTests.cs)
- [Console regressions](../../Test/Web/Diagnostics/ConsoleExceptionDiagnosticsTests.cs)

## Operational HTTP observations

OperationalHttpHandler measures request headers for explicitly reviewed Azure, GitHub, analysis and telemetry clients.
Counts/latency use finite source/stage/outcome/status-class labels. Unsuccessful responses, transport failures and
header acquisitions at least 1,000 ms warn at most once per family/outcome per 30 seconds; all occurrences are counted.
Streaming body lifetime belongs to collector metrics. No handler reads URLs, headers or payload content for telemetry.

Routine HTTP framework logging defaults to Warning. Override Logging:LogLevel:System.Net.Http.HttpClient, or a specific
approved category, to Information for a bounded investigation. Unknown category names still fail closed; this is not
permission for prefix-based category approval. Security/application audits keep their configured Information level.

HTTP warnings also retain numeric StatusCode, distinguishing conflicts/authorization failures from status-class
aggregates. The exact EF Core Query category is reviewed: unknown query templates retain category and numeric
EventCode through the safe module fallback, never SQL, parameters or provider prose.
