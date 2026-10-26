# Web adapters and operation state

Live SignalR delivery, independent Application operation scopes, connected-cloud admission and renderer-local replay
bookkeeping.

## Contents

| File                                                                         | Types or asset                                                                            | Responsibility                                                                                               |
|------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------|
| [ConnectedCloudDeploymentAdmission.cs](ConnectedCloudDeploymentAdmission.cs) | `ConnectedCloudDeploymentAdmission`, `CloudConnectionState`, `CloudDeploymentPreparation` | Scoped cloud connection preparation, finite CloudConnectionState and credential-hidden admission object.     |
| [ProjectTerminalReplayState.cs](ProjectTerminalReplayState.cs)               | `ProjectTerminalReplayState`                                                              | Owns bounded terminal buffering and replay deduplication for one renderer-owned project view.                |
| [RealTimeLogStreamer.cs](RealTimeLogStreamer.cs)                             | `RealTimeLogStreamer`                                                                     | Streams deployment logs and container metrics to project-specific SignalR groups.                            |
| [ScopedApplicationOperations.cs](ScopedApplicationOperations.cs)             | `ScopedApplicationOperations`                                                             | Executes independent application operations with fresh scoped dependencies instead of sharing circuit state. |

## Usage and integration

RealTimeLogStreamer implements ILogStreamer using project groups and stable callback shapes. ScopedApplicationOperations
creates/disposes a new async scope per independent call. ConnectedCloudDeploymentAdmission prepares connections without
exposing credential properties and delegates ready admission in a fresh scope. ProjectTerminalReplayState centralizes
bounded buffering/deduplication.

## Contracts and lifecycle

Live sends reapply text/metric validation at the final transport boundary. Caller cancellation and configured deadline
expiry are distinct; cancel actual pending writes. Default terminal/metric/notice delivery deadline is two seconds.
Saved logs remain recoverable if live delivery fails.

Cloud preparation preserves Azure-before-GitHub lookup order and Dashboard owner-fence rules. The preparation object is
operational/secret-bearing internally and must not be logged. ReplayState is renderer-owned: it retains 512 buffered
events, 2,000 live identities, negative live-only orders and overflow state. Pages still own SignalR/render/subscription
lifetimes.

## Related documentation

- [Parent module](../README.md)
- [Streaming contract](../../Application/Abstractions/Logging/README.md)
- [Groups/callbacks](../Hubs/README.md)
- [Admission policy](../../Application/Orchestration/Cloud/README.md)
- [Transport tests](../../Test/Web/Diagnostics/RealTimeLogStreamerTests.cs)
- [Scope tests](../../Test/Web/Hosting/ScopedApplicationOperationsTests.cs)
- [State tests](../../Test/Web/Projects/ProjectTerminalReplayStateTests.cs)
- [Connection tests](../../Test/Web/Projects/ConnectedCloudDeploymentAdmissionTests.cs)
