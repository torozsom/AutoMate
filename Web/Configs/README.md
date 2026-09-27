# Configs

Web dependency composition, authentication, options, and HTTP pipeline configuration.

## Source inventory

- `AppConfiguration.cs`
- `AzureSubscriptionResolver.cs`
- `JwtPayloadReader.cs`
- `ServiceConfiguration.cs`

## Boundary

Keep presentation behavior here. Components, hubs, routes, and services use Application contracts; concrete Infrastructure types are registered only in Web/Configs.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)
