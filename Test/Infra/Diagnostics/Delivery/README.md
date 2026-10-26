# Delivery and private-protocol tests

Sink isolation, durable confirmation, viewer leases, capability failures and gated real backend acceptance.

## Contents

| File                                                                           | Test or fixture types                                                                  | Responsibility                                                                                            |
|--------------------------------------------------------------------------------|----------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------|
| [DiagnosticDeliveryTests.cs](DiagnosticDeliveryTests.cs)                       | `DiagnosticDeliveryTests`, `TestStore`, `TestLive`                                     | Verifies sink deadlines, durable confirmations and later-event recovery without database payload writes.  |
| [RuntimeViewingTests.cs](RuntimeViewingTests.cs)                               | `RuntimeViewingTests`, `UnusedMetrics`, `RecordingGateway`, `TestClock`, `UnusedQuery` | Verifies live viewing is scoped, expiring, bounded, and saves replay independently of background consent. |
| [TelemetryGatewayCompatibilityTests.cs](TelemetryGatewayCompatibilityTests.cs) | `TelemetryGatewayCompatibilityTests`                                                   | Verifies private protocol compatibility independently of tenant history and external storage.             |
| [TelemetryIntegrationTests.cs](TelemetryIntegrationTests.cs)                   | `TelemetryIntegrationTests`, `DelayedSinks`, `TelemetryIntegrationFactAttribute`       | Opt-in PostgreSQL/Loki/Mimir acceptance plus TelemetryIntegrationFactAttribute gate.                      |

## Coverage and navigation

DiagnosticDeliveryTests verifies slow/failed sinks, caller cancellation, trace propagation and independent overflow
identity. RuntimeViewingTests verifies bounded/expiring deployment-scoped interest and automatic storage with legacy
flags disabled. GatewayCompatibilityTests checks capability caching and finite failure classifications.
TelemetryIntegrationTests targets actual PostgreSQL/Loki/Mimir.

## Fixtures and boundaries

Default cases use controlled sinks/HTTP and owned resources. Real acceptance is skipped without
AUTOMATE_TELEMETRY_TEST_DB. Its fixture applies migrations/seeds an isolated database and expects the Loki/Mimir
endpoints defined in TelemetryStorageTests.Specialized (loopback ports 24310 and 24909). Provisioning that isolated
stack is separate from merely setting the variable.

Real cases check selected backend filtering, disk receipt recovery, replay/numeric visibility, legacy write rejection
and complete-prefix/expiry behavior. Never point the gate at an application database or infer real backend success from
skipped discovery. Preserve durability when live delivery times out.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra.Diagnostics.Delivery
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Delivery implementation](../../../../Infrastructure/Diagnostics/Delivery/README.md)
- [Fixture endpoint/options source](../PolicyAndMetrics/TelemetryStorageTests.cs)
- [Contracts](../../../../Application/Abstractions/Diagnostics/Delivery/README.md)
- [Stack operations](../../../../docs/deployment-telemetry.md)
