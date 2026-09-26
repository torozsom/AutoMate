# Extensions

`Web/Extensions` contains reusable ASP.NET Core extension methods that support
Web startup composition. The current module owns automatic discovery and
registration of route endpoint classes.

This is a small infrastructure module. It does not define route behavior,
authentication handlers, middleware, or business workflows. Those concerns
remain in `Web/Routes`, `Web/Configs`, and `Services`.

## Structure

```text
Web/Extensions/
└── EndpointExtensions.cs
```

## Endpoint Discovery

`EndpointExtensions.AddEndpoints` scans an assembly for concrete,
non-abstract classes implementing `Web.Routes.IEndpoint`:

```csharp
builder.Services.AddEndpoints();
```

The default assembly is `Assembly.GetExecutingAssembly()`, which is the Web
assembly when called from `ServiceConfiguration`. A caller can provide an
explicit assembly for tests or a different endpoint module.

Discovery filters to:

- classes;
- non-abstract types;
- types assignable to `IEndpoint`.

Each discovered type is registered as a transient enumerable service:

```csharp
services.TryAddEnumerable(
    ServiceDescriptor.Transient(typeof(IEndpoint), endpointType));
```

`TryAddEnumerable` prevents duplicate descriptors for the same service and
implementation pair while allowing multiple endpoint implementations to
coexist.

## Mapping Lifecycle

Registration and mapping are intentionally separate:

```text
ServiceConfiguration.AddApplicationServices
    -> IServiceCollection.AddEndpoints
    -> build WebApplication
    -> AppConfiguration.UseApplicationPipeline
    -> app.Services.GetServices<IEndpoint>()
    -> endpoint.Map(app)
```

`AppConfiguration` resolves all registered `IEndpoint` instances and invokes
their `Map` method after the core hub and health-check mappings are added.
Endpoint classes therefore own their route definitions, authorization metadata,
and handler dependencies, while this module only discovers and registers
them.

Do not call `Map` during service registration. Route mapping requires the
built application and belongs in the request-pipeline phase.

## Deterministic Registration Order

Discovery sorts endpoint types by `GetEndpointRegistrationOrder`, then by
fully qualified type name:

| Endpoint type | Order | Reason |
|---|---:|---|
| `StaticAssetsEndpoint` | 0 | Static assets must be mapped before the Razor component endpoint. |
| Other endpoint types | 100 | Normal application endpoints. |
| `RazorComponentsEndpoint` | 200 | Razor component fallback should be mapped after concrete endpoints. |

The secondary full-name sort makes the order stable for all endpoints sharing
the same priority. This is important because endpoint mapping order can affect
routing, fallback behavior, and static asset performance.

If a new endpoint must precede or follow a fallback, update the explicit order
mapping rather than relying on namespace or file order. Prefer a narrowly
named ordering rule over broad numeric changes.

## Current Endpoint Set

The discovered endpoint classes currently include:

| Endpoint | Routes/responsibility |
|---|---|
| `StaticAssetsEndpoint` | Maps static assets anonymously. |
| `RazorComponentsEndpoint` | Maps the root Razor component tree with interactive server render mode. |
| `LoginEndpoint` | `POST /api/auth/login`; local cookie sign-in. |
| `LogoutEndpoint` | `POST /api/auth/logout`; cookie sign-out. |
| `GitHubLoginEndpoint` | `GET /api/auth/github-login`; GitHub OAuth challenge. |
| `AzureLoginEndpoint` | Azure connection initiation and tenant-specific callback flow. |

The endpoint implementations remain in `Web/Routes/Endpoints`; this module
must not duplicate or hard-code their route handlers.

## Endpoint Contract

`IEndpoint` defines one operation:

```csharp
void Map(IEndpointRouteBuilder app);
```

An endpoint class should:

- be concrete and discoverable;
- implement `IEndpoint`;
- map a cohesive route group;
- declare authorization/anonymous metadata at the route;
- resolve services through normal minimal-API parameter binding;
- pass `HttpContext.RequestAborted` to asynchronous work;
- keep business behavior in injected services.

Endpoint mapping should be explicit and easy to inspect. Avoid hidden route
registration from constructors or static initialization.

## Authorization Boundary

The application has an authenticated fallback policy configured in
`Web/Configs/ServiceConfiguration.cs`. Endpoint classes must explicitly mark
public routes with `.AllowAnonymous()` and protected routes with
`.RequireAuthorization()` when their intended accessibility differs from the
fallback.

Current examples:

- login and GitHub OAuth challenge routes allow anonymous access;
- logout requires authorization;
- Azure account connection requires authorization;
- static assets allow anonymous access;
- the Razor component endpoint relies on the application authorization and
  component-level rules.

Do not weaken global authorization in this module to make one route public.
Set route-specific metadata in the endpoint implementation.

## Security Conventions

Endpoint discovery itself does not validate or authorize requests. Security
belongs to the mapped endpoint and the configured middleware:

- preserve antiforgery requirements for state-changing form endpoints;
- use `LocalRedirect` for internal redirects where open redirects are
  possible;
- require authorization before using the current user's identity;
- do not place credentials or tokens in route registration code;
- pass cancellation tokens from `HttpContext.RequestAborted`;
- keep OAuth state, token exchange, and claim handling in the owning endpoint
  or configuration helper;
- do not expose exception details in endpoint responses.

`AddEndpoints` scans executable types using reflection. Do not load untrusted
assemblies into the scan input, and keep the default scan assembly limited to
the application's known endpoint assembly.

## Adding an Endpoint

To add a route group:

1. Create a concrete class under `Web/Routes/Endpoints` or a focused
   subdirectory.
2. Implement `IEndpoint`.
3. Add all related route mappings inside `Map`.
4. Apply `.AllowAnonymous()`, `.RequireAuthorization()`, antiforgery, and
   rate-limit metadata deliberately.
5. Inject service contracts through minimal-API parameters.
6. Pass request cancellation to asynchronous services.
7. Verify whether the endpoint must map before the Razor component fallback.
8. Add a dedicated registration priority only if the default order is
   insufficient.
9. Confirm the route appears in development Swagger when applicable.
10. Update the Web and Routes documentation when the public API surface
    changes.

No change to `EndpointExtensions` is needed for a normal new endpoint. The
reflection scan discovers it automatically.

## Testing Guidance

Test the extension itself with a small assembly or representative endpoint
types to verify:

- abstract classes are ignored;
- non-class types are ignored;
- unrelated classes are ignored;
- concrete `IEndpoint` implementations are registered once;
- explicit assembly input is honored;
- static assets sort before Razor components;
- Razor components sort after normal endpoints;
- same-priority endpoints sort by fully qualified name.

Application integration tests should verify:

- static assets are reachable anonymously;
- concrete API/auth endpoints remain reachable and retain their authorization;
- the Razor component fallback does not capture API routes;
- endpoint mapping occurs after required infrastructure is registered;
- duplicate registration calls do not create duplicate endpoint instances.

## Extension Method Conventions

Follow the existing style when adding extension methods:

- keep the extension class static;
- use a clear receiver type (`IServiceCollection`, `WebApplication`, or
  `WebApplicationBuilder`);
- return the receiver for fluent configuration methods;
- keep reflection and ordering logic private to the extension module;
- avoid broad exception swallowing;
- preserve deterministic behavior;
- document any ordering or lifetime assumptions.

Configuration extensions that register application services belong in
`Web/Configs/ServiceConfiguration.cs`; middleware and startup checks belong in
`Web/Configs/AppConfiguration.cs`. Do not turn `Web/Extensions` into a second
startup configuration file.

## File Map

| File | Purpose |
|---|---|
| `EndpointExtensions.cs` | Reflectively discovers concrete `IEndpoint` implementations, registers them as transient enumerables, and applies deterministic mapping priorities. |

## Related Documentation

- [`Web`](../README.md)
- [`Web/Configs`](../Configs/README.md)
- `Web/Routes` when route-level documentation is added
- [`Web/Components`](../Components/README.md)
