# Components

Blazor components and their presentation-specific code-behind files.
The project details page restores bounded terminal history for the latest deployment after joining its authorized
SignalR group, merges buffered live events by database cursor, and repeats that handshake after reconnecting.
It starts this handshake only after project loading has completed and terminal components have rendered; Blazor can
render the loading view before `OnInitializedAsync` finishes.
The shared `Terminal` component also queues writes until xterm finishes JavaScript initialization, so a fast replay
cannot disappear during the first render. Its pre-init queue has a fixed character limit and reports overflow.

## Source inventory

- `_Imports.razor`
- `App.razor`
- `Routes.razor`

## Boundary

Keep presentation behavior here. Components, hubs, routes, and services use Application contracts; concrete
Infrastructure types are registered only in Web/Configs.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)
