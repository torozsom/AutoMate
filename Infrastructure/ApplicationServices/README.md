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
project membership. Explicit consent edits create missing remote-project configuration with the existing .NET default;
missing local configuration or ownership returns false without changes. Sibling projects remain untouched. The existing
application-level
consent overload remains available. Analysis admission and provider egress continue to enforce current policy.
