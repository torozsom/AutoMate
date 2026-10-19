# Analysis result validation

Whole-answer validation/redaction and safe classifications for invalid, unavailable and access-denied provider results.

## Contents

| File                                                                                 | Types                                                                                                                          | Responsibility                                                                                                    |
|--------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------|
| [AnalysisProviderAccessDeniedException.cs](AnalysisProviderAccessDeniedException.cs) | `AnalysisProviderAccessDeniedException`                                                                                        | Non-retryable provider denial carrying only finite classification and a validated correlation identifier.         |
| [AnalysisResultValidator.cs](AnalysisResultValidator.cs)                             | `AnalysisResultValidator`, `InvalidAnalysisResultException`, `AnalysisResultRejection`, `AnalysisProviderUnavailableException` | Whole-answer validation, detached safe copies, finite rejection categories and safe unavailable/failure guidance. |

## Usage and integration

AnalysisResultValidator implements IAnalysisResultValidator and is used at both adapter and persistence boundaries. It
materializes safe independent arrays before callers mutate stored state. InvalidAnalysisResultException exposes finite
AnalysisResultRejection and stable failure guidance. AnalysisProviderUnavailableException carries a finite skip reason;
AnalysisProviderAccessDeniedException carries allowlisted denial metadata.

## Contracts and lifecycle

Version 1 results support a nonempty summary up to 4,096 characters, at most ten steps of 2,048 characters and twenty
evidence references of 512 characters. Empty lists are allowed. Version 2 requires AssessmentSections; each of four
sections permits at most six items of 768 characters. A blank version-2 overview receives fixed wording. Unknown/null
legacy schema is normalized based on sections; incompatible shapes are rejected.

All text passes through IDiagnosticRedactor with both raw/redacted limits. Provenance identifiers are bounded to 100
characters and rejected if redaction changes them or they resemble URLs/arbitrary payloads. Optional usage is
nonnegative; cost must fit numeric (18,8) with an uppercase three-letter currency and matching presence. Unknown
metadata remains null.

Validation rejects whole malformed/oversized answers; it does not guarantee narrative accuracy. Exact selected-evidence
membership is checked separately by AnalysisEvidence. Rejection codes cover
incomplete/refused/malformed/schema/limits/provenance/evidence/size failures, retaining legacy invalid_response
guidance.

Access denial uses fixed provider_access_denied guidance and safe provider/code/reason/request-ID fields. It does not
retain response prose or become a transient retry. Exception strings are not a substitute for categorized UI guidance.

## Related documentation

- [Parent module](../README.md)
- [Provider/result port](../../Abstractions/Ai/Results/README.md)
- [Citation membership](../Context/AnalysisEvidence.cs)
- [Protocol consumer](../../../Infrastructure/Ai/Providers/ResponsesAnalysisTransport.cs)
- [Publication consumer](../../../Infrastructure/Ai/Processing/DeploymentAnalysisProcessor.cs)
- [Validation regressions](../../../Test/Infra/Ai/ContextAndResults/AnalysisResultTests.cs)
- [Safe persistence tests](../../../Test/Infra/Ai/ContextAndResults/AnalysisPersistenceTests.cs)
