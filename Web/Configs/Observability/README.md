# Observability configuration

Validated exporter/resource options and authorization-result audit integration.

## Contents

| File                                                                 | Types or asset                  | Responsibility                                                                                      |
|----------------------------------------------------------------------|---------------------------------|-----------------------------------------------------------------------------------------------------|
| [OpenTelemetryOptions.cs](OpenTelemetryOptions.cs)                   | `OpenTelemetryOptions`          | Controls OpenTelemetry export without placing credentials in application settings.                  |
| [OpenTelemetryOptionsValidator.cs](OpenTelemetryOptionsValidator.cs) | `OpenTelemetryOptionsValidator` | Validates optional exporter configuration without exposing endpoint or resource-label values.       |
| [SecurityAuditResultHandler.cs](SecurityAuditResultHandler.cs)       | `SecurityAuditResultHandler`    | Audits authorization challenges/denials while preserving the framework's challenge/forbid behavior. |

## Usage and integration

OpenTelemetryOptions configures service/environment labels, console export and optional OTLP endpoint.
OpenTelemetryOptionsValidator checks typed settings before exporter registration. SecurityAuditResultHandler records
finite challenge/forbid outcomes while preserving framework behavior. Hosting wires SDK sources/meters/processors.

## Contracts and lifecycle

OTLP URLs must be bounded absolute HTTP (S) without user info, query/fragment or whitespace; credentials belong in
protected exporter settings, not URLs/tracked JSON. Service/environment labels are bounded and sanitized again by
SafeResourceDetector. Validation messages omit configured values. Export/resource changes require host restart.

Audit wrappers do not grant access or replace authorization results. Preserve fixed operation/outcome fields and
GUID-only approved correlation; never attach request/provider prose. Development console-export defaults are
configuration, not authorization to emit arbitrary state.

## Related documentation

- [Parent module](../README.md)
- [SDK policy](../../Observability/README.md)
- [Pre-provider boundary](../../../Infrastructure/Observability/README.md)
- [Registration](../Hosting/ServiceConfiguration.cs)
- [Option validation](../../../Test/Web/Hosting/HostingProfileRegistrationTests.cs)
- [Resource tests](../../../Test/Web/Hosting/MetricResourceSafetyTests.cs)
