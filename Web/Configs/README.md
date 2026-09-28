# Configs

Web dependency composition, authentication, options, and HTTP pipeline configuration. It configures OpenTelemetry for
AutoMate's logs, traces, metrics, and deployment diagnostic activity/meter sources. Development console export is on by
default; optional OTLP export is controlled by `OpenTelemetry` options and has no committed credentials.

## Source inventory

- `AppConfiguration.cs`
- `AzureSubscriptionResolver.cs`
- `JwtPayloadReader.cs`
- `ServiceConfiguration.cs`

## Boundary

Keep presentation behavior here. Components, hubs, routes, and services use Application contracts; concrete Infrastructure types are registered only in Web/Configs.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)
