# Diagnostic masking

Bounded safe-copy masking for normalized observations and standalone text.

## Contents

| File                                           | Types or asset       | Responsibility                                                         |
|------------------------------------------------|----------------------|------------------------------------------------------------------------|
| [DiagnosticRedactor.cs](DiagnosticRedactor.cs) | `DiagnosticRedactor` | Conservatively removes common credentials from normalized diagnostics. |

## Usage and integration

DiagnosticRedactor implements IDiagnosticRedactor. It masks event message/attributes/source/routing/cursor fields and
validates numeric/trace identity before downstream buffering or output. Shared Application terminal projection
extensions reuse standalone text redaction for replay/live/context/results.

## Contracts and lifecycle

Recognize credential-bearing JSON/environment/connection strings, authorization/cookies, private keys, URI user info,
JWTs and known token formats. Normalize structured sensitive keys/control sequences before matching. Fields over 128 Ki
characters or regex failures use fixed redaction markers; regex deadlines are finite. Normal output caps at 4,096
characters including omission notice; preserve blank/progress lines and idempotence.

Pattern/configured-value masking is not proof arbitrary unlabeled private prose is safe for external export. Terminal
payloads stay in authorized diagnostic storage/presentation; platform logging has a separate allowlist. Redact before
admission and recheck storage/readback boundaries rather than rely on final UI masking.

## Related documentation

- [Parent module](../README.md)
- [Contract/extensions](../../../Application/Abstractions/Diagnostics/Redaction/README.md)
- [Platform boundary](../../Observability/README.md)
- [Masking tests](../../../Test/Infra/Diagnostics/PolicyAndMetrics/DiagnosticRedactorTests.cs)
- [Context tests](../../../Test/Infra/Ai/ContextAndResults/AnalysisContextTests.cs)
