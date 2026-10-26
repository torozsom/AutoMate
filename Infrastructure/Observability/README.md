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
ConsoleExceptionDiagnostics receives exceptions separately for bounded redacted stderr snapshots. Web/Observability
retains SDK-specific exporter processors/views.

## Contracts and lifecycle

Ordinary providers receive reviewed literals, finite fields, safe internal GUID correlation and failure classes, without
arbitrary formatter output, SQL/URLs/provider prose or raw exception objects. Preserve category filters, levels and
event IDs; adding a template/category requires policy review. Do not replace the registered factory or introduce a raw
bypass.

Terminal payloads use the separate diagnostic redaction/storage path. The private host does not currently register OTel
exporters; future exporters need equivalent checks. These boundaries do not claim masking can identify all arbitrary
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
