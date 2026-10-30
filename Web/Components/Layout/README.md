# Layout and connection UI

Application shell/navigation and document-level circuit recovery.

`MainLayout.razor`, its code-behind and scoped CSS live here to match the preserved
`Web.Components.Layout.MainLayout` type. Shell contains navigation and the shared MainContent landmark.

## Contents

| Module                             | Responsibility                                           |
|------------------------------------|----------------------------------------------------------|
| [Connection](Connection/README.md) | Document-level Blazor reconnect/resume dialog.           |
| [Shell](Shell/README.md)           | Responsive layout/navigation and host capability checks. |

## Usage and integration

Shell renders MainLayout/NavMenu and capability-aware navigation. Connection presents the reconnect/resume dialog and
its collocated module. App hosts the reconnect UI while Routes chooses the layout for routed pages.

## Contracts and lifecycle

Layout controls presentation and local capability hints, not project authorization. Keep reconnecting the Blazor circuit
distinct from rejoining a project SignalR stream. Scoped styles/modules must keep their generated paths and
element/event contracts; component disposal rules are documented in each leaf guide.

## Related documentation

- [Parent module](../README.md)
- [Layout selection](../Routes.razor)
- [Document host](../App.razor)
- [Stream reconnection](../../Hubs/README.md)
