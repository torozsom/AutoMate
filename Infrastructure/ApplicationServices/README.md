# Infrastructure Application Services

Current EF-backed implementations of Application service contracts. This transitional module owns direct
AutoMateDbContext access.

Registration, verification, GitHub/Azure account persistence and project saves log internal actor/project GUIDs after
persistence. Consent changes and ownership denials use finite enabled/disabled states. Names, emails, external account
identifiers and tokens are not security-audit fields.

## Source inventory

- Submodules are documented by their own README files.

## Boundary

Infrastructure may reference Application and Domain, but never Web. It implements ports and owns provider-specific
behavior.

## Related documentation

- [Solution navigation map](../../navigation.md)

Data/Apps/ApplicationService persists exact-project AI consent only after filtering application ownership and C#
project membership. Explicit consent edits create missing remote-project configuration with the existing .NET default;
missing local configuration or ownership returns false without changes. Sibling projects remain untouched. The existing
application-level
consent overload remains available. Analysis admission and provider egress continue to enforce current policy.

## Workspace projections

Data/Apps/WorkspaceQuery implements IWorkspaceQuery through owner-scoped SQL projections and daily aggregate reads.
It loads no entity graph for inventory, isolates missing resource statistics, and performs no provider requests.

Workspace Overview also accepts absolute UTC windows, buckets recorded outcomes adaptively and uses IMetricExploration
for detailed/long-range resource projections. It never queries Azure and isolates resource failures from deployment
counts.

## Bounded project details

`Data/Apps/ProjectDetailsQuery.cs` implements both current-project read ports. It applies ownership to every query,
reads current metadata without older deployments, aggregates all recorded outcomes in SQL and pages history six rows
at a time. The split PostgreSQL reads share a repeatable snapshot (or retain a caller-owned transaction) so concurrent
updates cannot mix current state, totals and paging; isolated SQLite verification uses serializable reads. Equal newest
timestamps remain in the current graph; the history page keeps creation/identity descending
ordering. No additional index or migration is introduced. The old ApplicationService reads remain compatible.

Authentication and cloud admission/processing/monitoring now accept explicit execution clocks through additive
constructors. Existing constructors retain TimeProvider.System, and time is sampled at the original expiry, lease,
retry and completion decision points. Registration compensation and verification boundaries are unchanged.
