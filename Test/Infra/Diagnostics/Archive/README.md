# Archive and spool tests

Durability/restart, checksums, tenant scope, bounded replay, backfill/deletion and observed statistics.

## Contents

| File                                                                     | Test or fixture types                                                                                                                                               | Responsibility                                                                                                            |
|--------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------|
| [DeploymentDiagnosticReplayTests.cs](DeploymentDiagnosticReplayTests.cs) | `DeploymentDiagnosticReplayTests`, `SqliteReplayDbContext`                                                                                                          | Verifies retained diagnostic replay and rejection of new legacy payload writes.                                           |
| [DiskDeploymentArchiveTests.cs](DiskDeploymentArchiveTests.cs)           | `DiskDeploymentArchiveTests`                                                                                                                                        | Exercises permanent archive durability, isolation, redaction and bounded replay without provider calls.                   |
| [DiskTelemetrySpoolTests.cs](DiskTelemetrySpoolTests.cs)                 | `DiskTelemetrySpoolTests`                                                                                                                                           | Verifies disk-spool durability, bounds, redaction and restart behavior using temporary directories.                       |
| [SaaSTelemetryTests.cs](SaaSTelemetryTests.cs)                           | `SaaSTelemetryTests`, `Fixture`, `OrderedSqliteContext`, `RecordingGateway`, `CleanupArchiveProxy`, `BackfillTestLogger`, `EmptyLogs`, `EmptyMetrics`, `DailyQuery` | Verifies private-gateway history, aggregation, authorization and cleanup using controlled metadata and provider fixtures. |

## Coverage and navigation

DiskDeploymentArchiveTests verifies permanent event/metric durability, idempotence, corruption handling and
archive-required receipts. DiskTelemetrySpoolTests covers redaction before write/on read, single writer, bounds,
restart/identity, cursor binding and synthetic load. DeploymentDiagnosticReplayTests covers legacy read/expiry and
new-write rejection. SaaSTelemetryTests covers gateway history, archive fallback/context, cleanup/backfill and analytics
through controlled providers.

The historical assessment-context test inspects parsed numeric metric signals for deployment isolation. Its timestamp
deliberately contains `999`, so valid date fractions cannot be confused with the foreign deployment's numeric sample.

## Fixtures and boundaries

Use owned temporary directories and isolated metadata, not live Loki/Mimir or cloud providers. Do not conflate temporary
delivery buffering with permanent archive lifetime. Checksummed acknowledged files must not be silently rewritten to
mask corruption. History ownership/ordering/availability and no-PostgreSQL-payload fallback are observable contracts.

The spool load scenario is synthetic, not production capacity certification. AUTOMATE_TELEMETRY_EXTENDED_LOAD=1 extends
its normal five-second duration to sixty seconds.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra.Diagnostics.Archive
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Archive implementation](../../../../Infrastructure/Diagnostics/Archive/README.md)
- [Spool/legacy storage](../../../../Infrastructure/Diagnostics/Storage/README.md)
- [Real backend acceptance](../Delivery/README.md)
