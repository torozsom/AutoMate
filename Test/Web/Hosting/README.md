# Composition and export-policy tests

Production DI/options/profile validation, independent operation scopes and shared private-host safety.

## Contents

| File                                                                       | Test or fixture types                                                                                                        | Responsibility                                                                                              |
|----------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------|
| [HostingProfileRegistrationTests.cs](HostingProfileRegistrationTests.cs)   | `HostingProfileRegistrationTests`                                                                                            | Verifies profile-specific validation using the production Web composition root.                             |
| [MetricResourceSafetyTests.cs](MetricResourceSafetyTests.cs)               | `MetricResourceSafetyTests`                                                                                                  | Verifies resource and metric policy at real SDK and OTLP serialization boundaries.                          |
| [PlatformTelemetrySafetyTests.cs](PlatformTelemetrySafetyTests.cs)         | `PlatformTelemetrySafetyTests`, `SafeHttpFixture`, `NeverFormat`, `PrivacyTraceExporter`, `CaptureProvider`, `CaptureLogger` | Exercises policy at actual provider/export boundaries with adversarial untrusted metadata.                  |
| [ScopedApplicationOperationsTests.cs](ScopedApplicationOperationsTests.cs) | `ScopedApplicationOperationsTests`, `Probe`                                                                                  | Verifies independent operation lifetimes without relying on component private members.                      |
| [TelemetryHostSafetyTests.cs](TelemetryHostSafetyTests.cs)                 | `TelemetryHostSafetyTests`, `CaptureProvider`, `CaptureLogger`                                                               | Exercises the production private-host composition and authentication over loopback without external stores. |

## Coverage and navigation

HostingProfileRegistrationTests resolves real composition and validates AI/budgets/retries/OTLP/telemetry configuration
in both profiles. ScopedApplicationOperationsTests verifies overlapping/failing/canceled calls dispose independent
dependencies. PlatformTelemetrySafetyTests/MetricResourceSafetyTests inspect actual provider and OTLP serialization.
TelemetryHostSafetyTests hosts the production private-host authentication surface over loopback.

## Fixtures and boundaries

Tests do not start live cloud/collector/database/disk workers merely to resolve services. Synthetic invalid
endpoints/secret values must not appear in errors/export state. PostgreSQL model/migration acceptance is separate.
Preserve actual service lifetimes/aliases and safe typed fields; pre-provider and SDK processor boundaries are
complementary, not interchangeable.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Web.Hosting
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Composition](../../../Web/Configs/Hosting/README.md)
- [Option rules](../../../Web/Configs/Observability/README.md)
- [Scope boundary](../../../Web/Services/ScopedApplicationOperations.cs)
- [Shared policy](../../../Infrastructure/Observability/README.md)
- [Private host](../../../Telemetry/Program.cs)

ConfigurationBindingContractTests covers non-default CloudSaas, scheduling, Azure Logs, telemetry/spool, Docker,
email and GitHub App settings, including the binder's array-append behavior. EndpointDiscoveryTests protects the
complete ordered endpoint registry, idempotent registration and independent transient instances.
