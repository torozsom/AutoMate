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

`TelemetryStorage` selects PostgreSQL or Loki/Mimir and validates endpoints, transport, managed onboarding and quotas
at startup. [Telemetry hosting](../../docs/deployment-telemetry.md) documents operator secrets and retention controls.

Telemetry startup validation uses `TelemetryStorageOptionsValidator` to identify individual invalid settings without
exposing their values. Local IDE launches can import the complete pilot settings with
`deploy/telemetry/Configure-TelemetryDevelopment.ps1`.

Both hosting profiles require LokiMimir/DiskGateway. Web supplies those defaults before binding operator settings and
rejects PostgresOutbox even when explicitly configured. Endpoint and gateway credentials remain mandatory. Legacy
PostgreSQL reads and outbox draining continue during migration. See ../../docs/saas-telemetry.md.
