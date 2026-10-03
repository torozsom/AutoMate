# Configs

Web dependency composition, authentication, options, and HTTP pipeline configuration. It configures OpenTelemetry for
AutoMate's logs, traces, metrics, deployment diagnostic activity/meter sources, and security rate-limit rejection
events. Development console export is on by default; optional OTLP export is controlled by `OpenTelemetry` options and
has no committed credentials.

GitHub App credentials are required and validated at startup only in the SaaS hosting profile. Self-hosted pages can
resolve shared deployment services without configuring a GitHub App.

## Source inventory

- `AppConfiguration.cs`
- `AzureSubscriptionResolver.cs`
- `JwtPayloadReader.cs`
- `ServiceConfiguration.cs`

## Boundary

Keep presentation behavior here. Components, hubs, routes, and services use Application contracts; concrete
Infrastructure types are registered only in Web/Configs.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)
