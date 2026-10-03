# Routes

Minimal API endpoint contracts and endpoint implementations.
The SaaS-only GitHub App webhook endpoint accepts bounded HTTPS POST bodies and delegates signature verification and
minimal metadata persistence to Infrastructure.

## Source inventory

- `IEndpoint.cs`

## Boundary

Keep presentation behavior here. Components, hubs, routes, and services use Application contracts; concrete
Infrastructure types are registered only in Web/Configs.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)
