# Platform observability

`AutoMateTelemetry` owns stable deployment/security activity sources and meters. Metrics use finite source/kind/channel
labels; identifiers belong in approved log/trace correlation, never metric dimensions.

`OperationalLog` emits structured ILogger audit events using fixed `AuditOperation` and `AuditOutcome` names and stable
operation event IDs (1000-1005). `BeginCorrelation` accepts only deployment, analysis and project GUIDs. The event API
accepts no free-form messages, users, paths, exception objects, provider data or diagnostic context. Invalid enum values
produce Unknown rather than arbitrary names. Started/canceled events are Information; discarded events are Debug; denials/failures/invalid
results
are Warning; normal completion/unavailability is Information. These events do not authorize LLM egress.

Web configures actual OpenTelemetry scope capture, service resources and optional console/OTLP exporters. Existing
framework/legacy logs pass through Web/Observability allowlists before registered providers and SDK exporters. This
helper makes
no global safety guarantee and does not persist diagnostic logs or metrics. Loki/Mimir storage stays behind its ports.
Tests in `Web.Tests/OperationalLoggingTests.cs` capture actual SDK exports; worker tests verify terminal outcomes and
correlation without contacting providers.

Scope capture and record copying follow
the [OpenTelemetry .NET logging SDK documentation](https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/docs/logs/customizing-the-sdk/README.md).
OAuth emits Prepared after ticket processing, rather than claiming final cookie sign-in is complete.

## Deployment tracing

`DeploymentTracing` uses AutoMate.Deployments with fixed names, finite source/outcome labels and GUID correlation only.
Run/RunAsync preserve results, exception identity and cancellation, without exception events/descriptions or payload
tags.
Success predicates handle provider responses reporting failure without throwing. Children inherit the ambient parent;
async dispatch retains its ingestion parent. Standalone Docker builds inherit available ambient correlation. Global
framework trace attributes are sanitized by Web/Observability before export; spans with events/links are omitted.

AuditOutcome.RetryScheduled records successful durable retry scheduling using the existing finite audit contract.
It includes no provider payload, request text or exception details.

AuditOutcome.CanceledByOwner is an Information event emitted after durable cancellation commits, with GUID-only
analysis/deployment correlation. Caller/heartbeat cancellation continues to use the existing Debug Canceled outcome.

## Analysis workflow telemetry

`AnalysisTelemetry` owns `AutoMate.Analysis` activities and metrics without an OpenTelemetry SDK dependency.
Fixed claim/process/context/provider/renew/release/publish/retry spans carry deployment and analysis GUIDs when known.
Child steps inherit the processing trace. Outcomes are completed, failed, canceled, skipped, empty, discarded or
retry_scheduled; no provider/model names, prompts, context, results or exception text enter attributes.

`automate.analysis.operations` and `automate.analysis.duration` count/time steps by finite operation/outcome only.
Filter provider/failed for provider failures and retry/retry_scheduled for committed retries. Process duration is per
attempt, not total wall time across retry backoff. `automate.analysis.active` balances processing entry/exit on every
path. `automate.analysis.queue.wait` measures successful acquisition from initial creation or retry eligibility,
excluding scheduled backoff; it is not queue depth. Input/output token histograms record only validated known
provider-reported usage, including a subsequently discarded result; absent usage is not reported as zero. These are
operational measurements, not an exactly-once billing ledger. Web explicitly registers the source/meter and safe export
policy in both profiles. No diagnostic payload persistence or provider enablement is introduced.
