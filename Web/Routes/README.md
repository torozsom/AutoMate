# HTTP endpoint boundaries

Minimal API mapping contract and concrete routes for authentication, history, analysis, webhooks and framework assets.

## Contents

| File                         | Types or asset | Responsibility                                                                                                                                                          |
|------------------------------|----------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| [IEndpoint.cs](IEndpoint.cs) | `IEndpoint`    | An endpoint is a class that defines a set of routes for the application. It is responsible for mapping the routes to the appropriate endpoints in the request pipeline. |

| Module                           | Responsibility                                                         |
|----------------------------------|------------------------------------------------------------------------|
| [Endpoints](Endpoints/README.md) | History/analysis APIs, webhook ingress and framework/static endpoints. |

## Usage and integration

IEndpoint exposes Map (IEndpointRouteBuilder). EndpointExtensions discovers concrete classes; host pipeline maps them in
deterministic order. Endpoints handles protected read/mutation APIs and framework/static entries; Auth owns cookie/OAuth
HTTP interactions.

## Contracts and lifecycle

Resolve owner identity from the authenticated principal through Application contracts; do not accept an owner supplied
by a client. Keep route-level authentication and operation-level ownership separate. AI mutations explicitly validate
antiforgery; public webhook ingress instead uses signature verification and bounded HTTPS input. Endpoint registration
alone adds neither protection nor business validation.

## Related documentation

- [Parent module](../README.md)
- [Discovery/order](../Extensions/README.md)
- [Pipeline](../Configs/Hosting/README.md)
- [Analysis outcomes](../../Application/Abstractions/Ai/Execution/README.md)
- [History contracts](../../Application/Abstractions/Diagnostics/History/README.md)
