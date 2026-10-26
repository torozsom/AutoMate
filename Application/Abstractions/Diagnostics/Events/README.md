# Normalized diagnostic events

Versioned provider-neutral deployment observations and terminal projections for ingestion, replay and presentation.

## Contents

| File                                                                   | Types                                                                                                                                                                                                                                                                                    | Responsibility                                                                                                |
|------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------|
| [DeploymentDiagnosticEvent.cs](DeploymentDiagnosticEvent.cs)           | `DeploymentDiagnosticSource`, `DeploymentDiagnosticComponent`, `DeploymentDiagnosticStream`, `DeploymentDiagnosticSourceIdentity`, `DeploymentDiagnosticKind`, `DeploymentDiagnosticSeverity`, `DeploymentTerminalChannelKind`, `DeploymentTerminalChannel`, `DeploymentDiagnosticEvent` | Versioned event envelope, finite classifications, source identity and terminal routing.                       |
| [DeploymentTerminalLog.cs](DeploymentTerminalLog.cs)                   | `DeploymentTerminalLog`, `DeploymentTerminalHistory`                                                                                                                                                                                                                                     | Live/replay terminal projections, source metadata and bounded-history availability.                           |
| [IDeploymentDiagnosticPublisher.cs](IDeploymentDiagnosticPublisher.cs) | `IDeploymentDiagnosticPublisher`                                                                                                                                                                                                                                                         | Accepts normalized deployment observations for redaction, telemetry, bounded buffering, and safe UI delivery. |

## Usage and integration

Collectors publish DeploymentDiagnosticEvent through IDeploymentDiagnosticPublisher into DeploymentDiagnosticPipeline.
The event file defines source/component/stream identity, kind/severity and typed terminal channels.
DeploymentTerminalLog.FromEvent projects an event with its durable order/channel and source metadata;
DeploymentTerminalHistory wraps replay data and availability.

## Contracts and lifecycle

DeploymentDiagnosticEvent.SchemaVersion is 1. Optional metrics, event ID, trace/span, sequence and source cursor
preserve actual observations rather than invent missing metadata. Domain project/deployment identity and provider
correlation must be supplied by trusted collectors. The data records do not authorize IDs or redact text.

PublishAsync is the non-blocking ingestion boundary for deployment work, not proof of durable acceptance; use the
explicit durable publisher for checkpoint-sensitive paths. Redaction precedes sinks. FromEvent marks Azure annotation
events carrying an issue attribute as availability notices; it does not redact them. Preserve EarlierOmitted,
Availability and CanAdvanceCursor so storage delay is not presented as complete history.

## Related documentation

- [Parent module](../README.md)
- [Publisher implementation](../../../../Infrastructure/Diagnostics/Delivery/DeploymentDiagnosticPipeline.cs)
- [Durable acceptance](../Delivery/README.md)
- [Safe event handling](../Redaction/README.md)
- [Presentation transport](../../Logging/README.md)
- [Delivery tests](../../../../Test/Infra/Diagnostics/Delivery/DiagnosticDeliveryTests.cs)
