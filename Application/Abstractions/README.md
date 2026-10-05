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

Docker's `LocalDockerDiagnostics.cs` ports register immutable, non-secret ownership inventories and supervise daemon,
container-output and metrics sources without exposing Docker SDK models. `IDockerService` retains compatibility entry
points; Compose down accepts an optional deployment ID for correlated stop diagnostics.
