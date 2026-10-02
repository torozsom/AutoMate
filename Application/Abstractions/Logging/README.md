# Logging Port

The Application UI-streaming port implemented by Web. It accepts only redacted, normalized terminal data from the
deployment diagnostic dispatcher and routes it through stable, source-aware terminal channels; it is not a persistence,
telemetry, or AI-analysis API.

## Source inventory

- `ILogStreamer.cs`

Structured deployment events and their redaction/publishing ports live in the sibling
[`Diagnostics`](../Diagnostics/README.md) module.

## Boundary

Keep this module independent of Infrastructure and Web. Provider-facing work crosses an interface in
Application/Abstractions.

## Related documentation

- [Solution navigation map](../../../.agents/navigation.md)
