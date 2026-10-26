# Circuit reconnect dialog

Document-level Blazor reconnect/resume presentation and collocated JavaScript.

## Contents

| File                                                 | Types or asset    | Responsibility                                                                             |
|------------------------------------------------------|-------------------|--------------------------------------------------------------------------------------------|
| [ReconnectModal.razor](ReconnectModal.razor)         | `ReconnectModal`  | Presents Blazor circuit reconnect and resume actions through its scoped JavaScript module. |
| [ReconnectModal.razor.css](ReconnectModal.razor.css) | Scoped stylesheet | Scoped styling for ReconnectModal.                                                         |
| [ReconnectModal.razor.js](ReconnectModal.razor.js)   | Browser module    | Component-scoped Blazor reconnect/resume handlers.                                         |

## Usage and integration

ReconnectModal.razor defines the framework dialog/controls; its module observes components-reconnect-state-changed and
calls Blazor.reconnect/resumeCircuit. It rearms visibility retries when the host is unreachable and reloads when a
circuit cannot be recovered.

## Contracts and lifecycle

Keep framework element IDs/state classes and module registration aligned. This is document/circuit recovery, not project
stream subscription: the ProjectDetails page must independently reauthorize/rejoin SignalR. Listeners belong to the
mounted document recovery UI; do not claim the module provides a per-registration dispose API.

## Related documentation

- [Parent module](../README.md)
- [Document mounting](../../App.razor)
- [Stream recovery](../../Pages/Projects/README.md)
- [Connection-bound groups](../../../Hubs/README.md)
