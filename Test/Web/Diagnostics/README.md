# Transport and observability tests

Actual SDK audit/trace export, final SignalR masking/deadlines and hub disconnect semantics.

## Contents

| File                                                                       | Test or fixture types                                                                                                | Responsibility                                                                                       |
|----------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------|
| [ConsoleExceptionDiagnosticsTests.cs](ConsoleExceptionDiagnosticsTests.cs) | `ConsoleExceptionDiagnosticsTests`, `FailingWriter`, `BrokenException`, `CaptureProvider`, `CaptureLogger`, `Record` | Console-only exception snapshots remain bounded and detached from the ordinary provider boundary.    |
| [DeploymentTracingTests.cs](DeploymentTracingTests.cs)                     | `DeploymentTracingTests`, `TraceSnapshotExporter`, `ExportedSpan`                                                    | Checks real SDK span export, parentage and privacy without contacting a collector.                   |
| [LogHubCancellationTests.cs](LogHubCancellationTests.cs)                   | `LogHubCancellationTests`, `InterfaceStub`, `CallerContext`                                                          | Distinguishes expected subscription disconnects from genuine provider cancellation.                  |
| [OperationalLoggingTests.cs](OperationalLoggingTests.cs)                   | `OperationalLoggingTests`, `RepositoryHandler`, `PortProxy`, `SnapshotExporter`, `ExportedLog`                       | Checks real SDK exports and framework authorization responses without contacting a collector.        |
| [RealTimeLogStreamerTests.cs](RealTimeLogStreamerTests.cs)                 | `RealTimeLogStreamerTests`, `InterfaceStub`                                                                          | Verifies that deadlines cancel real transport writes while retaining browser callback compatibility. |
| [RequestAuditTests.cs](RequestAuditTests.cs)                               | `RequestAuditTests`, `Capture`, `Logger`                                                                             | Verifies safe request audit boundaries for successful, rejected and failed operations.               |

## Coverage and navigation

OperationalLoggingTests captures SDK records/scopes and actual login/authorization HTTP responses.
DeploymentTracingTests checks exported parentage/results/privacy. RealTimeLogStreamerTests records real client-proxy
arguments and canceled writes. LogHubCancellationTests separates navigation disconnect from active-provider
cancellation. RequestAudit/ConsoleException tests cover safe HTTP boundaries and detached bounded operator snapshots.
RequestAudit tests also exercise hostile/custom methods through an ordinary logger, and OperationalLogging tests
verify SMTP attempt events omit message content without the shared sanitization layer or a network connection.

## Fixtures and boundaries

Use synthetic ports/principals/credentials and controlled/loopback transport, not a remote collector or live SignalR
deployment. Assert exported/wire state rather than just helper strings. Preserve callback shapes, GUID/event/order
identity and cancellation identity while masking adversarial payloads. Console-only snapshots are separate from
metadata-only ordinary provider/export state; hostile getters/oversize/deep exceptions must not break logging.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Web.Diagnostics
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Final transport](../../../Web/Services/README.md)
- [Authorization/replay](../../../Web/Hubs/README.md)
- [SDK processors](../../../Web/Observability/README.md)
- [Shared logger/console policy](../../../Infrastructure/Observability/README.md)
- [Finite vocabulary](../../../Application/Diagnostics/README.md)
