# Live output port

Safe terminal and numeric observation delivery from deployment diagnostics to presentation clients.

## Contents

| File                               | Types          | Responsibility                                                                          |
|------------------------------------|----------------|-----------------------------------------------------------------------------------------|
| [ILogStreamer.cs](ILogStreamer.cs) | `ILogStreamer` | Represents a service responsible for streaming source-aware terminal data in real-time. |

## Usage and integration

ILogStreamer is implemented by Web RealTimeLogStreamer and consumed by the diagnostic delivery pipeline. Use
StreamTerminalLogAsync for normalized/redacted terminal projections and StreamTerminalNoticeAsync for operational
guidance. StreamDeploymentMetricsAsync carries typed resource observations; StreamContainerMetricsAsync preserves the
older CPU/memory callback.

## Contracts and lifecycle

This is a presentation transport, not a persistence, telemetry-storage or AI API. Terminal logs retain deployment
identity, durable order and optional event/source metadata. Typed metric delivery has a default no-op implementation for
older adapters; its presence does not guarantee support.

All sends accept cancellation. Implementations must cancel the actual pending transport write, including notices, rather
than leave detached work after timeout. Web applies live delivery deadlines and project authorization. Pass only safe
data; this interface does not redact arbitrary strings itself.

## Related documentation

- [Parent module](../README.md)
- [Web transport](../../../Web/Services/RealTimeLogStreamer.cs)
- [Delivery consumer](../../../Infrastructure/Diagnostics/Delivery/DeploymentDiagnosticPipeline.cs)
- [Event and history contracts](../Diagnostics/README.md)
- [Transport regressions](../../../Test/Web/Diagnostics/RealTimeLogStreamerTests.cs)
