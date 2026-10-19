# Telemetry delivery contracts

Durable gateway acceptance, pending history, private-host capabilities and expiring viewer interest.

## Contents

| File                                                         | Types                                                                                                             | Responsibility                                                                                      |
|--------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------|
| [IDeploymentRuntimeViewers.cs](IDeploymentRuntimeViewers.cs) | `IDeploymentRuntimeViewers`                                                                                       | Tracks expiring authorized viewing interest; viewer presence does not authorize diagnostic storage. |
| [ITelemetryGateway.cs](ITelemetryGateway.cs)                 | `ITelemetryGateway`, `TelemetryIngestRequest`, `TelemetryPendingHistory`, `IDurableDeploymentDiagnosticPublisher` | Durable ingestion/pending-read ports, gateway request/result shapes and checkpoint-safe publishing. |
| [TelemetryCapabilities.cs](TelemetryCapabilities.cs)         | `TelemetryCapabilities`                                                                                           | Private host protocol information; contains no tenant data, endpoints or credentials.               |

## Usage and integration

ITelemetryGateway accepts a normalized event/channel and returns the durable DeploymentLogEnvelope; ReadPendingAsync
exposes pending events plus loss/truncation information. IDurableDeploymentDiagnosticPublisher provides a
success-bearing publish operation for collectors that may advance checkpoints only after durability.
TelemetryIngestRequest is the gateway request shape.

## Contracts and lifecycle

IDeploymentRuntimeViewers tracks authorized viewing interest by connection/project/deployment. Renew/remove/viewer
checks are lease bookkeeping, not authorization or permission to store diagnostics. Runtime collection is automatic
under current policy; viewer presence does not grant AI egress.

TelemetryCapabilities uses protocol version 1 with required archive operations including channel-logs, metric-batch and
assessment support. The compatibility probe checks these before archive work. Pending history is bounded and may be
truncated or report dropped events. Never advance a provider cursor merely because fire-and-forget publishing returned.

## Related documentation

- [Parent module](../README.md)
- [Gateway client](../../../../Infrastructure/Diagnostics/Delivery/TelemetryGatewayClient.cs)
- [Capability checks](../../../../Infrastructure/Diagnostics/Delivery/TelemetryGatewayCompatibility.cs)
- [Durable publisher](../../../../Infrastructure/Diagnostics/Delivery/DeploymentDiagnosticPipeline.cs)
- [Viewer lease implementation](../../../../Infrastructure/Diagnostics/Runtime/DeploymentRuntimeViewers.cs)
- [Compatibility tests](../../../../Test/Infra/Diagnostics/Delivery/TelemetryGatewayCompatibilityTests.cs)
- [Viewer tests](../../../../Test/Infra/Diagnostics/Delivery/RuntimeViewingTests.cs)
