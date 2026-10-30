# Operational observability

Shared activity sources, meters, fixed audit events, tracing wrappers and deployment diagnostic options.

## Contents

| File                                                             | Types                                                                      | Responsibility                                                                            |
|------------------------------------------------------------------|----------------------------------------------------------------------------|-------------------------------------------------------------------------------------------|
| [AnalysisTelemetry.cs](AnalysisTelemetry.cs)                     | `AnalysisOperation`, `AnalysisOutcome`, `AnalysisTelemetry`, `Measurement` | Finite analysis operations/outcomes and disposable per-step activity/metric measurements. |
| [AutoMateTelemetry.cs](AutoMateTelemetry.cs)                     | `AutoMateTelemetry`                                                        | Owns the stable OpenTelemetry source names emitted by AutoMate deployment workflows.      |
| [DeploymentDiagnosticOptions.cs](DeploymentDiagnosticOptions.cs) | `DeploymentDiagnosticOptions`                                              | Controls the in-memory diagnostic delivery boundary.                                      |
| [DeploymentTracing.cs](DeploymentTracing.cs)                     | `DeploymentOperation`, `DeploymentTraceOutcome`, `DeploymentTracing`       | Fixed deployment operation/outcome vocabulary and result-preserving activity wrappers.    |
| [OperationalLog.cs](OperationalLog.cs)                           | `AuditOperation`, `AuditOutcome`, `OperationalLog`                         | Finite audit operations/outcomes, stable event IDs and GUID-only correlation scopes.      |

## Usage and integration

AutoMateTelemetry owns AutoMate.Security/AutoMate.Deployments sources and meters for deployment queues, cloud
control-plane snapshots, security and collector/pipeline measurements. DeploymentTracing wraps fixed deployment
operations. OperationalLog emits finite AuditOperation/AuditOutcome events with GUID correlation scopes.
AnalysisTelemetry instruments claim/process/context/provider/renew/release/publish/retry without an OpenTelemetry SDK
dependency.

## Contracts and lifecycle

Metric dimensions use finite labels; project/deployment/analysis GUIDs belong only in approved correlation, not metric
labels. No prompts, results, credentials, provider envelopes or arbitrary exception text belong in these helpers. Web
configures SDK collection/export and global log/trace sanitization; these classes alone do not make all host logging
safe or authorize egress.

OperationalLog uses stable event IDs 1000–1005 for defined operations, finite outcome names and Unknown for invalid
enums. Failed/Denied/Challenged/InvalidResult are Warning, Discarded is Debug, others Information. CanceledByOwner
follows committed cancellation; Prepared in OAuth does not claim cookie sign-in completed.

DeploymentTracing preserves action results/exceptions and ambient parenting; async wrappers distinguish caller
cancellation and optional success predicates. Activities use fixed names/outcomes without exception events or prose.
Dispose directly started activities and finish the outcome explicitly.

AnalysisTelemetry.Measurement must be disposed; Finish selects a finite outcome and disposal records
duration/operations, balances active processing and ends the activity once. Queue wait measures eligible-to-claim time,
not queue depth. Process duration is per attempt, excluding later backoff; token histograms record only known
nonnegative usage, including a later-discarded result. These metrics are not an exactly-once billing ledger.

DeploymentDiagnosticOptions defaults to buffer capacity 512, persistence timeout ten seconds and delivery timeout two
seconds. Host validation and pipeline consumers enforce bounds/deadlines; merely constructing options does not configure
a transport.

## Related documentation

- [Parent module](../README.md)
- [Host wiring and options](../../Web/Configs/Hosting/ServiceConfiguration.cs)
- [Export policies](../../Infrastructure/Observability/README.md)
- [Diagnostic pipeline](../../Infrastructure/Diagnostics/Delivery/DeploymentDiagnosticPipeline.cs)
- [Actual logging exports](../../Test/Web/Diagnostics/OperationalLoggingTests.cs)
- [Trace regressions](../../Test/Web/Diagnostics/DeploymentTracingTests.cs)
- [Metrics and operator interpretation](../../docs/ai-analysis-operations.md)

## Operational measurements and live cadence

OperationalMetrics emits finite source/stage/outcome/status-class counts and millisecond duration histograms without
SDK dependencies. Invalid labels normalize to Unknown/none. SignalR send measurements include every outcome regardless
of trace filtering; deployment async wrappers record persistence/provider/subscription durations. Streaming lifetime is
distinct from HTTP header acquisition. Live observation/coalescing/history-admission counters describe separate paths.

DeploymentDiagnostics:LiveMetricIntervalMilliseconds defaults to 1,000 and is validated to 100–5,000 by Web. Each Docker
collector retains one latest redacted observation, sends the first immediately and cancels/awaits trailing work on exit.
Both client callbacks remain compatible. Analysis polling remains two seconds after an empty claim.
