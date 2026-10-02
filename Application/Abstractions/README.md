# Abstractions

Application-owned outbound ports implemented by Infrastructure or Web.

## Source inventory

- Submodules are documented by their own README files.
- `GitHub/IGitHubService.cs` exposes workflow/job status and completed-run job/archive log downloads.

## Boundary

Keep this module independent of Infrastructure and Web. Provider-facing work crosses an interface in
Application/Abstractions.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)
