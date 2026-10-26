# AI provider protocol tests

Exact registered routes, synthetic Responses HTTP and configuration reload/cancellation.

## Contents

| File                                                                       | Test or fixture types                                                                                                                                   | Responsibility                                                                                            |
|----------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------|
| [AnalysisProviderSelectionTests.cs](AnalysisProviderSelectionTests.cs)     | `AnalysisProviderSelectionTests`, `Stub`                                                                                                                | Credential-free registry routing and fail-closed configuration tests, independent of workflow/UI changes. |
| [AzureOpenAiAnalysisProviderTests.cs](AzureOpenAiAnalysisProviderTests.cs) | `AzureOpenAiAnalysisProviderTests`                                                                                                                      | Azure-specific route/key/error checks plus the full shared Responses protocol contract.                   |
| [OpenAiAnalysisProviderTests.cs](OpenAiAnalysisProviderTests.cs)           | `OpenAiAnalysisProviderTests`, `ResponsesAnalysisProviderContractTests`, `ShutdownHandler`, `ShutdownContent`, `ShutdownStream`, `UnknownLengthContent` | Checks Responses parsing using synthetic HTTP fixtures; no provider credentials or calls are required.    |

## Coverage and navigation

AnalysisProviderSelectionTests checks unavailable/deferred resolution, current options, reload races, provenance
mismatch and duplicate names. OpenAiAnalysisProviderTests covers encoded/redacted input, bounds, actual model/usage,
evidence, body/error categories and no-send gates. AzureOpenAiAnalysisProviderTests adds exact
resource/key/deployment-alias behavior and shared v2 protocol cases.

## Fixtures and boundaries

All credentials/envelopes are synthetic; no live provider key or LLM request is needed. Handlers model headers/body
waits so reload can cancel actual local I/O. Assert fixed safe categories and no raw error/transport details, not
provider prose. Missing Azure configuration must not fall back to another provider. Protocol coverage does not approve a
live resource, pricing or regional processing.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra.Ai.Providers
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Adapters/transport](../../../../Infrastructure/Ai/Providers/README.md)
- [Validation categories](../../../../Application/Ai/Results/README.md)
- [Runtime gates](../../../../Application/Ai/Policy/README.md)
- [Synthetic HTTP helpers](../../TestSupport/README.md)
