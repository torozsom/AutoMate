# Application

Application contracts, coordination primitives, and outbound ports. This project references Domain only.

## Source inventory

- `Application.csproj`
- `Diagnostics/AutoMateTelemetry.cs` — stable, provider-independent activity and meter names for deployment and
  security telemetry.

## Boundary

Keep this module independent of Infrastructure and Web. Provider-facing work crosses an interface in
Application/Abstractions.

## Related documentation

- [Domain](../Domain/README.md)`n- [Infrastructure](../Infrastructure/README.md)`n- [Web](../Web/README.md)
