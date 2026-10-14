# Data

Application-facing user and project service contracts.

## Source inventory

- Submodules are documented by their own README files.

## Boundary

Keep this module independent of Infrastructure and Web. Provider-facing work crosses an interface in
Application/Abstractions.

## Related documentation

- [Solution navigation map](../../navigation.md)

Apps/IApplicationService exposes owner-authorized AI diagnostic egress consent. The exact-project overload requires
application ID, owner ID and C# project ID; Web uses it for the current deployment. Missing remote configuration is
created only during an explicit owner consent edit; no provider request is triggered. The legacy application-level
overload retains its first-configured-project behavior for existing callers. Consent does not enqueue analysis.

Apps/IWorkspaceQuery exposes bounded inventory and Overview metadata. Overview accepts absolute UTC chart windows and
returns optional detailed metric projections without provider credentials or entity graphs.

## Current project reads

`Apps/IProjectDetailsQuery.cs` defines owner-scoped current metadata, complete-history outcome totals and six-row
history
pages. `IProjectActionQuery` supplies only the current graph for deploy/stop actions. The detached domain-shaped graph
is a compatibility boundary for existing scanner/presentation consumers, not a tracked persistence aggregate.
Deployments tied at each C# project's newest timestamp are retained so existing latest-selection behavior stays stable.
`IApplicationService` continues to expose its existing full-graph APIs for legacy consumers.
