# Provider and result ports

Provider-neutral invocation models and the boundary for validating untrusted analysis answers.

## Contents

| File                                                       | Types                                                               | Responsibility                                                                               |
|------------------------------------------------------------|---------------------------------------------------------------------|----------------------------------------------------------------------------------------------|
| [IAnalysisResultValidator.cs](IAnalysisResultValidator.cs) | `IAnalysisResultValidator`                                          | Common validation/redaction boundary for provider output before persistence or presentation. |
| [ILlmAnalysisProvider.cs](ILlmAnalysisProvider.cs)         | `LlmAnalysisRequest`, `LlmAnalysisResponse`, `ILlmAnalysisProvider` | Already-redacted diagnostic context, retained only in memory for provider invocation.        |

## Usage and integration

ILlmAnalysisProvider consumes redacted in-memory LlmAnalysisRequest and returns untrusted LlmAnalysisResponse.
IAnalysisResultValidator validates/redacts a detached safe copy. Infrastructure adapters perform HTTP/protocol work; the
worker/persistence path validates again and checks evidence membership before publication.

## Contracts and lifecycle

LlmAnalysisRequest carries Context plus optional selected evidence IDs, deployment scope, trigger and assessment kind.
Null AllowedEvidenceReferences retains shape-only compatibility for legacy standalone calls; the real worker supplies a
detached catalog. Optional DeploymentId preserves constructors but actual provider adapters require deployment scope.
Neither optional field authorizes egress.

Responses include summary/steps/evidence and optional model revision, prompt/schema versions, usage, cost/currency and
structured sections. Unknown legacy values remain null; never infer model revisions or current prices. Validation
rejects the whole malformed/oversized result rather than truncate it. Application-owned
transient/unavailable/access-denied exceptions provide safe finite outcomes without raw provider bodies.

## Related documentation

- [Parent module](../README.md)
- [Validation and failure policy](../../../Ai/Results/README.md)
- [Exact evidence membership](../../../Ai/Context/AnalysisEvidence.cs)
- [Provider protocol](../../../../Infrastructure/Ai/Providers/ResponsesAnalysisTransport.cs)
- [Execution consumer](../../../../Infrastructure/Ai/Processing/DeploymentAnalysisProcessor.cs)
- [Result tests](../../../../Test/Infra/Ai/ContextAndResults/AnalysisResultTests.cs)
