# Data

Application-facing user and project service contracts.

## Source inventory

- Submodules are documented by their own README files.

## Boundary

Keep this module independent of Infrastructure and Web. Provider-facing work crosses an interface in
Application/Abstractions.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)

Apps/IApplicationService exposes owner-authorized AI diagnostic egress consent. The exact-project overload requires
application ID, owner ID and C# project ID; Web uses it for the current deployment. The legacy application-level
overload retains its first-configured-project behavior for existing callers. Consent does not enqueue analysis.
