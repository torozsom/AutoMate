# Infrastructure Application Services

Current EF-backed implementations of Application service contracts. This transitional module owns direct
AutoMateDbContext access.

## Source inventory

- Submodules are documented by their own README files.

## Boundary

Infrastructure may reference Application and Domain, but never Web. It implements ports and owns provider-specific
behavior.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)
