# Infrastructure

EF Core persistence, external-system adapters, and Application-port implementations.

## Source inventory

- `Infrastructure.csproj`

## Boundary

Infrastructure may reference Application and Domain, but never Web. It implements ports and owns provider-specific
behavior.

## Related documentation

- [Domain](../Domain/README.md)`n- [Application](../Application/README.md)`n- [Web](../Web/README.md)
