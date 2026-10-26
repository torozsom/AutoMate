# Diagnostic redaction boundary

Safe-copy event/text redaction and shared terminal projection policy.

## Contents

| File                                             | Types                                    | Responsibility                                                                            |
|--------------------------------------------------|------------------------------------------|-------------------------------------------------------------------------------------------|
| [DiagnosticRedaction.cs](DiagnosticRedaction.cs) | `DiagnosticRedaction`                    | Applies the shared text policy to every string in terminal transport/history projections. |
| [IDiagnosticRedactor.cs](IDiagnosticRedactor.cs) | `IDiagnosticRedactor`, `RedactionResult` | Redacts externally sourced diagnostic text and attributes before they reach any sink.     |

## Usage and integration

IDiagnosticRedactor returns RedactionResult with a safe event and redacted-value count, or bounds/masks standalone text.
DiagnosticRedaction.RedactTerminal applies the same policy to replay/live terminal fields. Infrastructure
DiagnosticRedactor implements credential/content masking; history, transport and AI consumers reuse the boundary.

## Contracts and lifecycle

RedactTerminal copies message/channel/source-instance/source-cursor fields with bounds and preserves durable
order/event/project/deployment identity. It accepts trace/span IDs only at canonical hexadecimal lengths, rejects
all-zero IDs and normalizes valid values to lowercase. It does not authorize owners or fabricate missing correlation.

Call redaction before buffering, logging/export or UI transport, not only at final rendering. Safe-copy semantics avoid
mutating caller observations. The interface does not define every masking rule; consult the implementation/tests when
extending supported provider payloads.

## Related documentation

- [Parent module](../README.md)
- [Masking implementation](../../../../Infrastructure/Diagnostics/Redaction/DiagnosticRedactor.cs)
- [AI evidence consumer](../../../Ai/Context/README.md)
- [Result redaction](../../../Ai/Results/README.md)
- [Masking regressions](../../../../Test/Infra/Diagnostics/PolicyAndMetrics/DiagnosticRedactorTests.cs)
- [Context redaction tests](../../../../Test/Infra/Ai/ContextAndResults/AnalysisContextTests.cs)
