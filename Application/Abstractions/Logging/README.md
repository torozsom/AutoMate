# Logging Port

The Application logging port used by deployment producers and implemented by Web.

## Source inventory

- `ILogStreamer.cs`

## Boundary

Keep this module independent of Infrastructure and Web. Provider-facing work crosses an interface in Application/Abstractions.

## Related documentation

- [Solution navigation map](../../../.agents/navigation.md)
