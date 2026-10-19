# Endpoint registration extensions

Deterministic discovery of concrete IEndpoint implementations for host composition.

## Contents

| File                                           | Types or asset       | Responsibility                                                                        |
|------------------------------------------------|----------------------|---------------------------------------------------------------------------------------|
| [EndpointExtensions.cs](EndpointExtensions.cs) | `EndpointExtensions` | Extension methods for auto-discovering and registering endpoints in the DI container. |

## Usage and integration

EndpointExtensions.AddEndpoints scans the supplied assembly (or the executing Web assembly), discovers nonabstract
IEndpoint classes and registers transient enumerable services with TryAddEnumerable. AppConfiguration later resolves and
calls their Map methods.

## Contracts and lifecycle

Mapping order is static assets first, ordinary endpoints next and Razor components last, with ordinal full-type-name
ordering within each group. Adding a class is sufficient only when it implements the contract and lies in the scanned
assembly. Reflection type-load failures propagate; discovery is not authentication, endpoint execution or a background
worker.

## Related documentation

- [Parent module](../README.md)
- [Endpoint contract](../Routes/README.md)
- [Registration consumer](../Configs/Hosting/ServiceConfiguration.cs)
- [Mapping consumer](../Configs/Hosting/AppConfiguration.cs)
