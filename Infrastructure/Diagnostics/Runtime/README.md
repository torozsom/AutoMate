# Runtime viewer interest

Bounded expiring process-local tracking for authorized live-view connections.

## Contents

| File                                                       | Types or asset             | Responsibility                                                                     |
|------------------------------------------------------------|----------------------------|------------------------------------------------------------------------------------|
| [DeploymentRuntimeViewers.cs](DeploymentRuntimeViewers.cs) | `DeploymentRuntimeViewers` | Bounds process-local live collection leases; stores no log content or credentials. |

## Usage and integration

DeploymentRuntimeViewers implements IDeploymentRuntimeViewers with TimeProvider-based expiry. Authorized Web joins renew
connection/project/deployment interest; disconnect/leave removes it. Consumers can check HasViewers without retaining
diagnostic content.

## Contracts and lifecycle

Tracking is bounded to 4,096 connection/project pairs and expires after 45 seconds without renewal. It is neither
cross-instance durable presence nor authentication/storage permission. Current runtime collection is automatic for
active deployments, so viewer absence is not a stop-collection policy. Keep renew/remove lifecycle paired and store no
tokens/messages.

## Related documentation

- [Parent module](../README.md)
- [Viewer contract](../../../Application/Abstractions/Diagnostics/Delivery/README.md)
- [Authorized consumer](../../../Web/Hubs/LogHub.cs)
- [Collection policy](../../ApplicationServices/Orchestration/Local/README.md)
- [Expiry tests](../../../Test/Infra/Diagnostics/Delivery/RuntimeViewingTests.cs)
