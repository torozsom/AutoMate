# Web

Blazor Server presentation, HTTP endpoints, SignalR, and the composition root.

## Source inventory

- `appsettings.Development.json`
- `appsettings.json`
- `Program.cs`
- `Web.csproj`
- `Web.csproj.user`

## Boundary

Keep presentation behavior here. Components, hubs, routes, and services use Application contracts; concrete Infrastructure types are registered only in Web/Configs.

## Related documentation

- [Application](../Application/README.md)`n- [Infrastructure](../Infrastructure/README.md)
