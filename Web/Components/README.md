# Components

Blazor components and their presentation-specific code-behind files.
The project details page restores bounded terminal history for the latest deployment after joining its authorized
SignalR group, merges buffered live events by database cursor, and repeats that handshake after reconnecting.
It starts this handshake only after project loading has completed and terminal components have rendered; Blazor can
render the loading view before `OnInitializedAsync` finishes.
The shared `Terminal` component also queues writes until xterm finishes JavaScript initialization, so a fast replay
cannot disappear during the first render. Its pre-init queue has a fixed character limit and reports overflow.
Dashboard and project details read the process-local queue state, showing “Queued...” until a deployment or stop job
starts. State changes notify both views without exposing the queued request's credentials.
In SaaS mode, cloud submissions use the durable Application admission service. Project details polls the authorized
run phase and incremental redacted terminal history so status and logs catch up across AutoMate instances.
In SaaS mode, cloud submissions use the durable Application admission service. Project details polls the authorized
run phase and incremental redacted terminal history so status and logs catch up across AutoMate instances.

## Source inventory

- `_Imports.razor`
- `App.razor`
- `Routes.razor`

## Boundary

Keep presentation behavior here. Components, hubs, routes, and services use Application contracts; concrete
Infrastructure types are registered only in Web/Configs.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)
