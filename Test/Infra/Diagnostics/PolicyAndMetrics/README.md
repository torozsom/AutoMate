# Diagnostic policy and metric tests

Masking/queue bounds, trusted write/transport configuration, numeric units and durable metric lookup.

## Contents

| File                                                     | Test or fixture types     | Responsibility                                                                            |
|----------------------------------------------------------|---------------------------|-------------------------------------------------------------------------------------------|
| [DiagnosticRedactorTests.cs](DiagnosticRedactorTests.cs) | `DiagnosticRedactorTests` | Verifies credential masking, terminal-control removal and bounded diagnostic metadata.    |
| [MetricExplorationTests.cs](MetricExplorationTests.cs)   | `MetricExplorationTests`  | Exercises window limits and durable metric lookup recovery without cloud calls.           |
| [TelemetryStorageTests.cs](TelemetryStorageTests.cs)     | `TelemetryStorageTests`   | Credential-free storage protocol, quota configuration, and numeric-unit regression tests. |

## Coverage and navigation

DiagnosticRedactorTests checks supported secret/control formats, safe metadata, idempotence and pre-queue
masking/overflow. TelemetryStorageTests checks direct-batch masking, mixed-tenant/unsupported metric rejection before
HTTP, finite quotas/validation, Loki/Mimir protocol and throttling. MetricExplorationTests checks UTC/calendar windows,
indexed sample filtering/restart and checksum-based lookup recovery.

## Fixtures and boundaries

Use synthetic handlers, temporary archives and controlled options, not live backends. Preserve missing values/native
units and stable identity across masking. Invalid option failures must not echo supplied secrets/URLs. The Specialized
options helper is also reused by explicitly gated acceptance tests; the helper itself does not provision that stack.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra.Diagnostics.PolicyAndMetrics
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Masking implementation](../../../../Infrastructure/Diagnostics/Redaction/README.md)
- [Limits/options](../../../../Infrastructure/Diagnostics/Policy/README.md)
- [Metric semantics](../../../../Infrastructure/Diagnostics/Metrics/README.md)
- [Opt-in backend tests](../Delivery/README.md)
