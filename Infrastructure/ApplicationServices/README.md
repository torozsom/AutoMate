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

- [Solution navigation map](../../.agents/navigation.md)

Data/Apps/ApplicationService persists exact-project AI consent only after filtering application ownership and C#
project membership. Missing configuration/ownership returns false without changes. The existing application-level
consent overload remains available. Analysis admission and provider egress continue to enforce current policy.
