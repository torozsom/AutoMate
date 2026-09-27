# Configs

`Web/Configs` is the startup composition boundary for the Web project. It
keeps `Program.cs` small by grouping dependency registration, security
configuration, middleware ordering, OAuth callback helpers, and startup
infrastructure checks into focused extensions and helpers.

The module configures the application; it does not implement business
workflows. Domain behavior remains in `Services`, shared contracts and
entities remain in `Core`, and HTTP route mapping remains in `Web/Routes`.

## Structure

```text
Web/Configs/
├── ServiceConfiguration.cs
├── AppConfiguration.cs
├── JwtPayloadReader.cs
└── AzureSubscriptionResolver.cs
```

`Program.cs` invokes the composition methods in this order:

```csharp
builder.AddApplicationServices();
var app = builder.Build();
app.UseApplicationPipeline();
await app.InitializeInfrastructureAsync();
await app.RunAsync();
```

Preserve this high-level sequence. Services must be registered before
building the application; middleware and endpoints must be mapped before
startup checks and request processing begin.

## `ServiceConfiguration`

`ServiceConfiguration` exposes the `WebApplicationBuilder` extension
`AddApplicationServices`. It composes the application in six stages:

1. logging and health checks;
2. strongly typed options;
3. infrastructure;
4. security;
5. presentation;
6. domain services and endpoint discovery.

The method returns the original builder for fluent startup composition.

### Logging and health checks

The Web host registers logging and the health-check service collection. The
health endpoint itself is mapped by `AppConfiguration` at `/health`.

Health checks should remain suitable for readiness/liveness probing. Do not
add expensive scans or provider calls to the base registration unless the
health endpoint is intentionally changed to represent those dependencies.

### Strongly typed options

`AddConfigurations` binds:

| Options type | Configuration section | Consumer |
|---|---|---|
| `EmailOptions` | `Email` | `Services.Email` |
| `DockerOptions` | `Docker` | `Services.Docker` |

Add new options bindings here only when the options type is used by the Web
composition or a registered service. Keep option validation and service
behavior in the owning module where practical.

### Infrastructure registrations

`AddInfrastructure` registers:

| Registration | Configuration/behavior |
|---|---|
| `AutoMateDbContext` | Pooled EF Core context using PostgreSQL and `DefaultConnection`. |
| PostgreSQL naming | `UseSnakeCaseNamingConvention`. |
| `IDistributedCache` | StackExchange Redis using `ConnectionStrings:Redis` and `AutoMate_` instance prefix. |
| `IGitHubService`/`GitHubService` | Typed `HttpClient` with the standard .NET resilience handler. |

The same distributed cache is used for GitHub repository caching and
tenant-specific Azure OAuth state. Redis availability and cache fallback
behavior belong to the consuming service; this module only registers the
provider.

The database context is pooled. Services must follow EF Core scoped-context
usage and must not retain a context beyond the operation scope.

### Security registrations

`AddSecurity` configures forwarded headers, antiforgery, Data Protection,
authentication, authorization, and global rate limiting.

#### Forwarded headers

The application trusts `X-Forwarded-For` and `X-Forwarded-Proto` and clears
known proxy/network restrictions:

```csharp
options.KnownIPNetworks.Clear();
options.KnownProxies.Clear();
```

This assumes deployment behind a trusted reverse proxy that controls and
sanitizes forwarded headers. Do not use this configuration on an untrusted
direct-facing host without reviewing the proxy trust model. If the hosting
topology changes, update both the proxy configuration and this registration.

#### Antiforgery

Antiforgery services are registered for form and endpoint protection. Login,
logout, and registration flows use the framework antiforgery mechanisms where
their markup/endpoints require them.

#### Data Protection

Data Protection keys are persisted through `AutoMateDbContext` and use
`AutoMate` as the application name. Persistence keeps authentication cookies,
SignalR-related protected values, and other protected payloads valid across
container restarts when the database remains available.

Do not change the application name or key persistence location casually; doing
so can invalidate existing cookies or protected data.

#### Cookie authentication

The default authentication scheme and challenge scheme are configured as:

- default scheme: ASP.NET Core cookie authentication;
- default challenge: `GitHub`;
- HTTP-only cookie;
- `SameSite=Lax`;
- `SecurePolicy=SameAsRequest`;
- login path: `/login`;
- logout path: `/api/auth/logout`;
- access-denied path: `/login`.

Cookie settings are security-sensitive. Any change must consider OAuth
redirects, HTTPS termination, cross-site behavior, and local development.

#### GitHub OAuth

GitHub is configured with:

- required `Authentication:GitHub:ClientId`;
- required `Authentication:GitHub:ClientSecret`;
- callback path `/signin-github`;
- scopes for email, repository access, workflow access, and package
  read/write operations;
- `OnCreatingTicket` callback that calls
  `IAuthService.CreateOrUpdateGitHubUserAsync`.

The OAuth callback extracts the provider ID, username, email, avatar URL, and
access token. Missing GitHub email is replaced with the provider's noreply
address. Token persistence and encryption are owned by the Auth/Data services,
not by this configuration module.

Missing required client credentials fail startup with an explicit
`InvalidOperationException`. Do not replace these failures with empty
defaults.

#### Microsoft/Azure OAuth

Microsoft OAuth is registered under the `"Microsoft"` scheme for Azure account
connection. The authority tenant comes from
`Authentication:Microsoft:TenantId` when it is one of:

- `common`;
- `organizations`;
- `consumers`;
- a valid GUID.

Invalid or missing values fall back to `organizations`.

The scheme requires:

- `Authentication:Microsoft:ClientId`;
- `Authentication:Microsoft:ClientSecret`.

It uses `/signin-microsoft`, requests OpenID/profile/email/offline access and
Azure Management scope, saves tokens, and processes ticket creation through
`ProcessMicrosoftLoginAsync`.

The ticket callback:

1. reads the AutoMate user identifier from the temporary authentication
   properties;
2. extracts Azure identity fields from OAuth claims or the ID-token payload;
3. exchanges a refresh token for an Azure Management-scoped token when
   needed;
4. resolves a preferred subscription through
   `AzureSubscriptionResolver`;
5. links the Azure account through `IAuthService.LinkAzureAccountAsync`.

`CompleteMicrosoftConnectionAsync` prevents the Azure connection flow from
replacing the existing AutoMate cookie. It handles the response and redirects
to `/dashboard`.

The tenant-specific personal-account flow is implemented by
`Routes/Endpoints/Auth/AzureLoginEndpoint.cs`, which uses the same helpers and
cache infrastructure but an explicit authorization-code exchange.

#### Authorization

`AddCascadingAuthenticationState` enables authentication state in Blazor.
Authorization uses a fallback policy requiring an authenticated user. Pages
that are intentionally public must explicitly opt out, for example with
`[AllowAnonymous]`.

Do not weaken the fallback policy to accommodate a single public route.
Change the route's explicit authorization metadata instead.

#### Rate limiting

A global fixed-window limiter allows 100 requests per minute per partition.
Authenticated requests partition by stable name identifier or identity name;
anonymous requests partition by remote IP address. Rejected requests receive
HTTP 429.

The partition key and limit affect all HTTP traffic, including endpoints and
framework requests. Review login, OAuth, SignalR, health, and static asset
behavior before changing the policy.

Do not log raw tokens or treat an arbitrary client-provided header as an
identity partition key.

### Presentation registrations

`AddPresentation` registers:

- Razor Components with interactive server components;
- SignalR;
- endpoint API exploration;
- Swagger generation.

The SignalR hub itself is mapped by `AppConfiguration`, and Razor/static
endpoint discovery is provided by `Web/Routes`.

### Domain-service registrations

The `IServiceCollection` extension `RegisterDomainServices` registers the
application modules used by the Web layer:

| Lifetime | Services |
|---|---|
| Scoped | `IAuthService`, password hasher, `IDockerService`, local/cloud deployment orchestrators, Azure orchestrators/runtime streamer, `ILogStreamer`, application/user services, scanners, templating, email |
| Singleton | `IDeploymentJobQueue`, `IDeploymentStatusNotifier` |
| Hosted | `DeploymentJobWorker`, `DeploymentCleanupHostedService` |

The lifetimes are intentional:

- scoped services can depend on scoped EF and request state;
- the deployment queue and status notifier coordinate across requests;
- hosted services process background deployment/cleanup work for the
  application lifetime.

When adding a domain service, register its interface here rather than
constructing it in components or endpoints. Confirm lifetime compatibility
before injecting it into a singleton or hosted service.

### Endpoint registration

`AddEndpoints` is called after domain services are registered. Endpoint
discovery is owned by `Web/Routes`, not by this module. Keep route mapping
modular and avoid adding endpoint lambdas to `Program.cs`.

## `AppConfiguration`

`AppConfiguration` exposes two `WebApplication` extensions:

- `UseApplicationPipeline`;
- `InitializeInfrastructureAsync`.

### Middleware order

`UseApplicationPipeline` currently applies:

1. forwarded headers;
2. development Swagger/Swagger UI or production exception handling/HSTS;
3. status-code re-execution to `/not-found`;
4. HTTPS redirection;
5. routing;
6. authentication;
7. rate limiting;
8. authorization;
9. antiforgery;
10. `/loghub` SignalR mapping;
11. `/health` mapping;
12. dynamically discovered `IEndpoint` mappings.

Order is part of the behavior. Changes should be evaluated against:

- reverse-proxy scheme/host handling;
- exception and 404 behavior;
- authentication and authorization;
- rate-limiter partition identity;
- antiforgery validation;
- SignalR negotiation;
- endpoint authorization metadata.

`MapStaticAssets().AllowAnonymous()` is supplied by
`StaticAssetsEndpoint`, which is discovered and mapped in the final endpoint
loop. Static assets therefore remain publicly readable even though the
fallback authorization policy requires authentication elsewhere.

The application includes a comment warning that HTTPS redirection may loop
when a reverse proxy terminates TLS incorrectly. Keep proxy forwarding and
HTTPS behavior aligned.

### Environment-specific behavior

Development:

- enables Swagger and Swagger UI;
- does not install the production exception handler/HSTS branch.

Non-development:

- re-executes unhandled exceptions at `/Error`;
- enables HSTS.

Do not expose detailed exception data by changing the production branch.

### SignalR and health endpoints

The log hub is mapped at `/loghub`. Its authorization and project-group
validation are implemented by `Web/Hubs/LogHub`; this module only maps the
route.

Health checks are mapped at `/health` for platform probes. Keep this endpoint
lightweight and avoid requiring an authenticated user unless the deployment
environment explicitly expects authenticated probes.

### Startup database probe

`InitializeInfrastructureAsync` creates a scoped provider, resolves
`AutoMateDbContext`, and calls `Database.CanConnectAsync`:

- successful connectivity logs information;
- a false result logs a warning;
- an exception logs a critical error.

The method does not apply migrations and does not stop the application when
the database is unavailable. Migration execution remains an explicit
deployment/maintenance operation.

Use a scope for startup-resolved scoped services and preserve cancellation or
startup-failure policy if this check is expanded.

## `JwtPayloadReader`

`JwtPayloadReader.GetStringValue` decodes a JWT payload segment and extracts a
string property. It:

- performs Base64URL normalization and padding;
- parses the JSON payload;
- returns `null` for malformed structure, JSON, or Base64 data.

It **does not validate the JWT signature, issuer, audience, expiry, or token
algorithm**. It is only a convenience extractor for identity fields in an
OAuth response that has already been obtained through the configured OAuth
flow. Never use it as an authentication or authorization check.

When changing claims, preserve fallback handling between OAuth user claims and
the ID token. Do not log the complete token or payload.

## `AzureSubscriptionResolver`

`AzureSubscriptionResolver.GetDefaultSubscriptionIdAsync` calls:

```text
GET https://management.azure.com/subscriptions?api-version=2022-12-01
Authorization: Bearer <Azure management token>
```

It returns the first subscription whose state is `Enabled`, or the first
returned subscription as a fallback. Missing tokens, unsuccessful responses,
missing arrays, and missing IDs produce `null`.

The resolver uses `IHttpClientFactory`, passes cancellation, and does not
persist credentials. Callers decide whether a missing subscription is a
recoverable OAuth error or a failed account-link operation.

## Configuration Keys

The Web host expects these configuration categories. Values should come from
environment variables, user secrets, deployment secret stores, or another
protected provider; do not copy credentials from development settings into
source control.

| Key | Used by |
|---|---|
| `ConnectionStrings:DefaultConnection` | PostgreSQL `AutoMateDbContext`. |
| `ConnectionStrings:Redis` | Distributed cache for GitHub data and Azure OAuth state. |
| `Authentication:GitHub:ClientId` | GitHub OAuth client. |
| `Authentication:GitHub:ClientSecret` | GitHub OAuth client secret. |
| `Authentication:Microsoft:ClientId` | Microsoft/Azure OAuth client. |
| `Authentication:Microsoft:ClientSecret` | Microsoft/Azure OAuth client secret. |
| `Authentication:Microsoft:TenantId` | Optional Microsoft authority tenant. |
| `Email:*` | `EmailOptions` binding. |
| `Docker:*` | `DockerOptions` binding. |

`appsettings.Development.json` is a development configuration example.
Treat all credential-like values in configuration files as secrets and use
user-secrets or environment variables instead.

## Security Rules

- Keep required OAuth credentials fail-fast; never add silent placeholder
  credentials.
- Treat OAuth access/refresh tokens, client secrets, database connection
  strings, and Data Protection keys as sensitive.
- Do not use `JwtPayloadReader` as token validation.
- Review forwarded-header trust whenever deployment topology changes.
- Preserve antiforgery, HTTPS, cookie, fallback authorization, and rate-limit
  configuration when adding endpoints.
- Keep Azure OAuth state in distributed cache with expiration and one-time
  removal.
- Do not include tokens in logs, error messages, query strings, or generated
  documentation.
- Keep static assets and health probes free of sensitive data.
- Use explicit authorization metadata for public endpoints.

## Extension Guidance

When adding a new configuration concern:

1. Decide whether it belongs to options binding, infrastructure, security,
   presentation, domain registration, middleware, or endpoint mapping.
2. Add it to the smallest focused private method in `ServiceConfiguration`
   or `AppConfiguration`.
3. Preserve `Program.cs` as a startup composition script.
4. Choose a lifetime compatible with dependencies and hosted-service usage.
5. Add required configuration validation with an explicit failure message.
6. Trace middleware ordering and endpoint authorization requirements.
7. Pass cancellation through external calls and startup checks.
8. Update the relevant project/module README when registrations change.

Do not add business logic to configuration extensions. If a registration
requires complex transformation, extract a focused service/helper with
tests and keep the startup method declarative.

## Testing Guidance

Focused tests should cover:

- valid and invalid Microsoft tenant normalization;
- GitHub and Microsoft OAuth ticket field mapping;
- missing OAuth credentials failing fast;
- JWT payload extraction without treating malformed input as valid;
- enabled-subscription selection and first-subscription fallback;
- rate-limit partition selection for authenticated and anonymous requests;
- middleware and endpoint registration in development and production;
- startup database connectivity success, false, and exception paths;
- service lifetimes and hosted-service registrations.

Integration tests should verify that:

- public static assets remain accessible;
- anonymous routes remain explicitly public;
- authenticated routes receive the fallback policy;
- `/health` and `/loghub` are mapped as expected;
- forwarded headers and HTTPS behavior match the hosting proxy.

## File Map

| File | Purpose |
|---|---|
| `ServiceConfiguration.cs` | Builder/service registration, OAuth setup, security, infrastructure, presentation, domain services, and endpoint discovery. |
| `AppConfiguration.cs` | Middleware pipeline, hub/health/endpoint mapping, and startup database connectivity check. |
| `JwtPayloadReader.cs` | Unvalidated JWT payload claim extraction for OAuth identity fallback. |
| `AzureSubscriptionResolver.cs` | Azure Management API subscription selection. |
| `Program.cs` | Calls the configuration extensions in startup order. |

## Related Documentation

- [`Web`](../README.md)
- [`Web/Components`](../Components/README.md)
- [`Web/Routes`](../Routes/README.md) when route documentation is added
- [`Services/Auth`](../../Services/Auth/README.md)
- [`Services/Data`](../../Services/Data/README.md)
- [`Services/GitHub`](../../Services/GitHub/README.md)
- [`Services/Azure`](../../Services/Azure/README.md)
- [`Services/Docker`](../../Services/Docker/README.md)
- [`Services/Email`](../../Services/Email/README.md)
- [`Services/Orchestration`](../../Services/Orchestration/README.md)
