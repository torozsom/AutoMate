# Telemetry options and admission policy

Validated storage settings and bounded short-lived server-owned identity projections.

## Contents

| File                                                                       | Types or asset                                          | Responsibility                                                                               |
|----------------------------------------------------------------------------|---------------------------------------------------------|----------------------------------------------------------------------------------------------|
| [TelemetryAdmissionPolicy.cs](TelemetryAdmissionPolicy.cs)                 | `TelemetryAdmissionPolicy`                              | Short-lived, bounded server-owned identity mapping, never derived from a tenant HTTP header. |
| [TelemetryProjectPolicyCache.cs](TelemetryProjectPolicyCache.cs)           | `TelemetryProjectPolicy`, `TelemetryProjectPolicyCache` | Detached project ownership and compatibility flags used by telemetry admission checks.       |
| [TelemetryStorageOptions.cs](TelemetryStorageOptions.cs)                   | `TelemetryStorageOptions`                               | Specialized storage configuration; secrets must come from environment or a secret store.     |
| [TelemetryStorageOptionsValidator.cs](TelemetryStorageOptionsValidator.cs) | `TelemetryStorageOptionsValidator`                      | Provides safe, setting-specific startup errors for incomplete telemetry configuration.       |

## Usage and integration

TelemetryStorageOptions/Validator provide safe setting-specific startup validation. TelemetryAdmissionPolicy resolves
project/deployment ownership; TelemetryProjectPolicyCache supplies detached ownership/compatibility flags through
short-lived scoped database reads. Pipeline/gateway consumers apply these before accepting events.

## Contracts and lifecycle

Resolve tenants from trusted metadata, never client tenant headers. Caches retain GUID projections/flags rather than
payloads, credentials or provider errors; five-second freshness bounds stale state. Viewer presence is not storage
authorization.

Raw option defaults retain legacy Postgres/PostgresOutbox values for compatibility; current Web profiles require the
private DiskGateway path and reject payload fallback. Do not mistake constructing options for supported deployment
configuration. Limits cover buffer/rate/series, batches and HTTP concurrency; secrets/endpoints must not appear in
validator errors.

## Related documentation

- [Parent module](../README.md)
- [Delivery contracts](../../../Application/Abstractions/Diagnostics/Delivery/README.md)
- [Admission consumers](../Delivery/README.md)
- [Host constraints](../../../Web/Configs/Hosting/ServiceConfiguration.cs)
- [Policy tests](../../../Test/Infra/Diagnostics/PolicyAndMetrics/TelemetryStorageTests.cs)
- [Startup tests](../../../Test/Web/Hosting/HostingProfileRegistrationTests.cs)
