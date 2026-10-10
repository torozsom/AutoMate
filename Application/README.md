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

## Deployment history update (2026-10-08)

IDeploymentArchive defines durable deployment log append, cursor reads, bounded metric aggregation/import and project
deletion. IDeploymentDetailsService authorizes exact deployment metadata. AI results expose deployment-specific paged
listing.

## Workspace read models

Data/Apps/IWorkspaceQuery defines owner-scoped Overview analytics and twenty-row project inventory projections.
Counts use recorded completion outcomes separately from runtime status; resource rows expose sample-weighted observed
values.

## Metric exploration

The Diagnostics MetricExploration contracts expose absolute UTC chart windows, recorded sufficient statistics and
independent numeric pagination. IWorkspaceQuery also accepts a frozen window. Chart windows range from five minutes to
five calendar years; AI selection limits remain separate. See ../docs/metric-exploration.md.
