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

- [Domain](../Domain/README.md)
- [Infrastructure](../Infrastructure/README.md)
- [Web](../Web/README.md)

## Deployment analysis

[AI ports](Abstractions/Ai/README.md) own analysis admission/readback/cancellation, token leases, context/evidence,
egress authorization, shared budget reservations, provider responses and readiness. [AI policies](Ai/README.md) own
bounded context, result validation, fixed skip guidance, retries and exact monetary limits. Infrastructure supplies
EF/provider implementations; Application contains no HTTP/provider SDK or EF dependency. Diagnostics defines shared
workflow activities/meters and safe audits. See [architecture and onboarding](../docs/ai-analysis.md).

`Diagnostics/OperationalLog.cs` owns fixed platform audit operations/outcomes and GUID-only ILogger correlation scopes.
See [platform observability](Diagnostics/README.md) for the event contract, consumers and limits.

`Diagnostics/DeploymentTracing.cs` owns fixed child span names, GUID correlation and safe provider/sink outcomes, shared
by Web and Infrastructure without an OpenTelemetry SDK dependency in Application.
