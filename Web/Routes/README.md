# Routes

`Web/Routes` owns the HTTP endpoint definitions for AutoMate. Each route group
is represented by a concrete `IEndpoint` implementation and is discovered and
registered through `Web/Extensions/EndpointExtensions`.

The module maps static assets, the Razor component application, local
authentication, GitHub OAuth initiation, and Azure account connection flows.
It does not implement domain behavior, persistence, provider SDK logic, or
Blazor page rendering.

## Structure

```text
Web/Routes/
├── IEndpoint.cs
└── Endpoints/
    ├── StaticAssetsEndpoint.cs
    ├── RazorComponentsEndpoint.cs
    └── Auth/
        ├── LoginEndpoint.cs
        ├── LogoutEndpoint.cs
        ├── GitHubLoginEndpoint.cs
        └── AzureLoginEndpoint.cs
```

## Endpoint Discovery and Mapping

`IEndpoint` defines the route registration contract:

```csharp
void Map(IEndpointRouteBuilder app);
```

`ServiceConfiguration.AddApplicationServices` calls
`IServiceCollection.AddEndpoints`, which reflects over the Web assembly and
registers concrete `IEndpoint` implementations as transient enumerable
services. `AppConfiguration.UseApplicationPipeline` resolves those services
and invokes `Map(app)` after core health and SignalR mappings.

The registration order is deterministic:

1. `StaticAssetsEndpoint`;
2. normal API/auth endpoints;
3. `RazorComponentsEndpoint`.

Static assets must be mapped before the Razor component fallback. API and
OAuth routes must also be mapped before the component application can handle
unmatched browser requests.

A normal new endpoint requires no changes to the discovery extension. Create a
concrete `IEndpoint` implementation and let reflection discover it.

## Endpoint Inventory

| Endpoint | Route | Access | Responsibility |
|---|---|---|---|
| `StaticAssetsEndpoint` | framework static assets | Anonymous | Serves files from `wwwroot` and framework asset mappings. |
| `RazorComponentsEndpoint` | Razor component application | Component/application policy | Maps `App` with interactive server render mode. |
| `LoginEndpoint` | `POST /api/auth/login` | Anonymous | Validates local credentials and creates the AutoMate cookie. |
| `LogoutEndpoint` | `POST /api/auth/logout` | Authenticated | Clears the AutoMate cookie and redirects home. |
| `GitHubLoginEndpoint` | `GET /api/auth/github-login` | Anonymous | Starts the GitHub OAuth challenge. |
| `AzureLoginEndpoint` | `/api/auth/azure-login*` | Start authenticated; callback flow | Connects an Azure account to the current AutoMate user. |

The SignalR `/loghub` and `/health` routes are mapped directly by
`AppConfiguration`, not by an `IEndpoint` class. Keep those infrastructure
routes separate from normal HTTP route groups unless their ownership changes.

## Static Assets

`StaticAssetsEndpoint` maps application and framework assets with
`MapStaticAssets().AllowAnonymous()`.

Static assets are public by design. They include CSS, JavaScript, Bootstrap
files, the favicon, and other browser resources. Never put credentials,
tokens, private configuration, generated deployment files, or user data under
`wwwroot`.

This endpoint is assigned the highest precedence by endpoint discovery so
asset requests are handled before the Razor component application. See
`Web/wwwroot/README.md` for asset loading and browser-runtime details.

## Razor Components

`RazorComponentsEndpoint` maps the root `App` component:

```csharp
app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();
```

The route is intentionally mapped last among discovered endpoints. The
component router in `Components/Routes.razor` selects page components and
default layouts after the request reaches the Razor application.

Do not add page-specific API behavior to this endpoint. Page routes belong in
Razor components; HTTP APIs belong in focused endpoint classes.

## Local Login

`LoginEndpoint` handles `POST /api/auth/login` with form-bound `email` and
`password` values.

Flow:

1. reject blank email or password with a local redirect and URL-encoded error;
2. call `IAuthService.LoginAsync` with the request cancellation token;
3. redirect back to `/login` when authentication fails;
4. create name identifier, name, and email claims for a successful user;
5. issue a non-persistent cookie using the configured cookie scheme;
6. redirect to `/`.

The endpoint is anonymous because unauthenticated users need access to login.
The corresponding Razor form includes an antiforgery token. Preserve that
protection when changing the form or endpoint.

Use `Results.LocalRedirect` for internal redirects. Do not accept a
user-controlled return URL without explicit validation.

The endpoint owns cookie issuance but not password verification. Password
validation and account rules belong to `Services.Auth`.

## Logout

`LogoutEndpoint` handles `POST /api/auth/logout` and requires authorization.
It signs out the configured cookie authentication scheme and locally redirects
to `/`.

Keep logout as a state-changing POST and preserve antiforgery protection from
the calling form. Do not turn it into an anonymous GET route.

## GitHub OAuth Initiation

`GitHubLoginEndpoint` handles `GET /api/auth/github-login` anonymously and
returns an authentication challenge for the `"GitHub"` scheme with `/` as the
post-login redirect.

The OAuth provider registration and creating-ticket persistence flow are
configured in `Web/Configs/ServiceConfiguration.cs`. This endpoint only
starts the challenge.

Do not store GitHub tokens in the endpoint or expose provider credentials in
the redirect response.

## Azure Account Connection

`AzureLoginEndpoint` owns both Azure connection entry and the
tenant-specific authorization-code callback.

### Standard connection flow

`GET /api/auth/azure-login` requires authorization. It reads the current
AutoMate `ClaimTypes.NameIdentifier` and starts the configured `"Microsoft"`
OAuth challenge. The identifier is stored in authentication properties under
`automate_user_id` so the OAuth ticket callback can associate the Azure
account with the initiating AutoMate user.

The configured Microsoft OAuth event handler in `ServiceConfiguration`
processes the standard callback, resolves an Azure subscription, links the
account through `IAuthService`, and redirects to `/dashboard`.

### Tenant-specific flow

When `tenantId` is provided, the endpoint performs an explicit authorization
code flow for personal Microsoft-backed Azure tenants:

1. require a valid GUID tenant ID;
2. read the Microsoft client ID from configuration;
3. build an absolute callback URL;
4. generate 32 random bytes encoded as a URL-safe state token;
5. store `{ userIdentifier, tenantId, redirectUri }` in distributed cache for
   ten minutes;
6. redirect to the tenant-specific Microsoft authorization endpoint.

The callback is:

```text
GET /api/auth/azure-login/callback
```

It:

1. redirects provider errors to `/dashboard?azure_error=...`;
2. requires a code and state;
3. loads and removes the one-time state from distributed cache;
4. validates the cached state payload;
5. exchanges the authorization code for Azure Management-scoped tokens;
6. reads account identity fields from the ID-token payload;
7. resolves the first enabled Azure subscription, falling back to the first
   returned subscription;
8. links the Azure account with `IAuthService.LinkAzureAccountAsync`;
9. redirects to `/dashboard`.

State is stored under the `azure-oauth-state:` prefix and is removed before
the token exchange to prevent replay. The state cache uses `IDistributedCache`,
so the flow works across application instances when Redis is configured.

### Azure error behavior

The callback returns a bad request for missing/invalid code, state, or cached
payload. Provider and token exchange failures redirect to the dashboard with
an `azure_error` query value intended for user-facing status display.

Do not include access tokens, refresh tokens, client secrets, or raw provider
responses in redirect messages or logs. Keep error text concise and safe.

### Azure token exchange

The explicit flow posts to:

```text
https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token
```

with authorization-code grant fields, the configured client credentials,
redirect URI, and OpenID/profile/email/offline/Azure Management scopes.
Requests use `IHttpClientFactory` and `RequestAborted`.

The returned access token is used to query Azure subscriptions through
`AzureSubscriptionResolver`. Access and refresh tokens are handed to the
Auth/Data services for protected persistence; this endpoint does not persist
them directly.

## Authorization and Antiforgery

The Web host has an authenticated fallback authorization policy. Endpoint
classes should still make intended accessibility explicit:

- `.AllowAnonymous()` for login and OAuth challenge routes;
- `.RequireAuthorization()` for logout and Azure connection initiation;
- explicit asset anonymity for static files.

The Azure callback does not require a normal user cookie because its identity
comes from the short-lived one-time OAuth state. Its cached state must remain
bound to the initiating AutoMate user.

State-changing form endpoints must preserve antiforgery protection. If an
endpoint binds form data, ensure the corresponding Razor form emits an
`AntiforgeryToken` and that middleware/metadata remains configured.

## Error, Redirect, and Cancellation Rules

- Pass `HttpContext.RequestAborted` to Auth, cache, and HTTP operations.
- Use local redirects for internal destinations.
- URL-encode user-facing query values.
- Return bad requests for structurally invalid OAuth callbacks.
- Redirect provider failures to a safe internal status page.
- Do not expose stack traces, tokens, client secrets, or raw provider payloads.
- Let unexpected service failures remain observable through the host's
  exception/logging pipeline unless a safe user-facing recovery path exists.

Do not convert failed authentication into a successful-looking response. The
login route must redirect with an error; Azure connection must either link the
account or report a clear failure.

## Security Rules

- Keep OAuth client IDs/secrets in configuration providers, not source files.
- Keep OAuth state random, short-lived, distributed, and one-time.
- Bind OAuth state to the initiating AutoMate user.
- Validate tenant IDs before building provider URLs.
- Use HTTPS in production and ensure forwarded headers are configured
  consistently behind a reverse proxy.
- Keep static assets anonymous but free of sensitive data.
- Preserve local redirects to avoid open redirects.
- Do not treat `JwtPayloadReader` as JWT validation; it only extracts claims
  from a payload already returned by the provider flow.
- Review OAuth scopes before adding new permissions.

## Adding a Route

When adding a new endpoint:

1. Create a cohesive concrete `IEndpoint` implementation.
2. Place it under `Endpoints` or a focused subdirectory such as `Auth`.
3. Map all related routes inside `Map`.
4. Add explicit authorization/anonymous metadata.
5. Bind services through minimal-API parameters.
6. Pass request cancellation to async work.
7. Keep business logic in `Services`.
8. Use safe internal redirects and validated query values.
9. Check whether mapping order must precede the Razor component endpoint.
10. Update this README and the Web route map.

No manual registration is normally required because endpoint discovery is
reflection-based. If a new endpoint must change precedence, update the
ordering rule in `Web/Extensions/EndpointExtensions.cs` and add a regression
test.

## Testing Guidance

Test each endpoint with:

- expected anonymous/authenticated access;
- missing and malformed form/query values;
- cancellation from `RequestAborted`;
- safe redirect behavior;
- antiforgery requirements for state-changing forms;
- service failure and user-safe error responses.

OAuth integration tests should cover:

- standard GitHub challenge initiation;
- standard Microsoft challenge initiation;
- invalid tenant IDs;
- missing/expired/replayed Azure state;
- state-user mismatch;
- provider errors;
- invalid token responses;
- missing Azure account identifiers;
- no enabled subscription;
- successful account linking and dashboard redirect.

Route integration tests should verify:

- static assets are mapped before the Razor fallback;
- API/auth routes are not captured by Razor components;
- `/loghub` and `/health` remain mapped by application configuration;
- endpoint discovery registers each route group exactly once.

## File Map

| File | Purpose |
|---|---|
| `IEndpoint.cs` | Contract for class-based endpoint mapping. |
| `Endpoints/StaticAssetsEndpoint.cs` | Anonymous static asset mapping. |
| `Endpoints/RazorComponentsEndpoint.cs` | Interactive Razor component application mapping. |
| `Endpoints/Auth/LoginEndpoint.cs` | Local form login and cookie issuance. |
| `Endpoints/Auth/LogoutEndpoint.cs` | Authenticated cookie sign-out. |
| `Endpoints/Auth/GitHubLoginEndpoint.cs` | GitHub OAuth challenge initiation. |
| `Endpoints/Auth/AzureLoginEndpoint.cs` | Standard and tenant-specific Azure account connection flow. |

## Related Documentation

- [`Web`](../README.md)
- [`Web/Extensions`](../Extensions/README.md)
- [`Web/Configs`](../Configs/README.md)
- [`Web/Components`](../Components/README.md)
- [`Web/Hubs`](../Hubs/README.md)
- [`Services/Auth`](../../Services/Auth/README.md)
- [`Services/Data`](../../Services/Data/README.md)
- [`Services/GitHub`](../../Services/GitHub/README.md)
- [`Services/Azure`](../../Services/Azure/README.md)
