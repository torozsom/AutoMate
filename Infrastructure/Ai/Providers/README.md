# Analysis provider adapters

Explicit exact-route registration and shared bounded Responses transport for direct OpenAI and Azure OpenAI.

## Contents

| File                                                             | Types or asset                                                                          | Responsibility                                                                                       |
|------------------------------------------------------------------|-----------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------|
| [AnalysisProviderCatalog.cs](AnalysisProviderCatalog.cs)         | `AnalysisProviderRegistration`, `AnalysisProviderCatalog`, `ConfiguredAnalysisProvider` | Registered exact-route provider selection and deferred adapter resolution.                           |
| [AzureOpenAiAnalysisProvider.cs](AzureOpenAiAnalysisProvider.cs) | `AzureOpenAiAnalysisProvider`                                                           | Azure OpenAI Responses v1 adapter using an explicit resource API key and deployment alias.           |
| [AzureOpenAiOptions.cs](AzureOpenAiOptions.cs)                   | `AzureOpenAiOptions`                                                                    | Azure resource identity and protected API-key configuration; never used as result or telemetry data. |
| [OpenAiAnalysisProvider.cs](OpenAiAnalysisProvider.cs)           | `OpenAiAnalysisProvider`                                                                | Direct OpenAI adapter retaining its exact regional route and independent credential setting.         |
| [ResponsesAnalysisTransport.cs](ResponsesAnalysisTransport.cs)   | `ResponsesAnalysisTransport`                                                            | Shared bounded Responses protocol for the explicitly registered OpenAI and Azure OpenAI adapters.    |

## Usage and integration

AnalysisProviderCatalog selects a registered adapter from current policy; ConfiguredAnalysisProvider defers
construction. OpenAiAnalysisProvider retains its approved regional endpoint/key. AzureOpenAiAnalysisProvider uses an
exact resource route, deployment alias and separate AzureOpenAi options. ResponsesAnalysisTransport shares
request/schema, bounded parsing and safe failure translation.

## Contracts and lifecycle

Do not approve arbitrary lookalike base URLs. Azure options bind AiAnalysis:AzureOpenAi ResourceName/ApiKey; account
OAuth credentials are not OpenAI API keys. Direct OpenAI credentials remain separate.

Transport independently rechecks egress and validates input/output/schema/evidence. Bodies cap at 131,072 bytes;
optional usage/model/cost provenance stays unknown unless supplied. PromptVersion identifies instructions, not a
provider revision.

AI option changes cancel local headers/body I/O and reject late responses. Policy shutdown becomes Unavailable, not
transient retry. Replica reload propagation and remote processing/charges cannot be recalled; environment-only settings
need restart. Access denial uses fixed guidance/allowlisted metadata. Transient exceptions carry bounded retry
information without raw response prose.

## Related documentation

- [Parent module](../README.md)
- [Result validation](../../../Application/Ai/Results/README.md)
- [Egress/retry policy](../../../Application/Ai/Policy/README.md)
- [Direct adapter tests](../../../Test/Infra/Ai/Providers/OpenAiAnalysisProviderTests.cs)
- [Azure tests](../../../Test/Infra/Ai/Providers/AzureOpenAiAnalysisProviderTests.cs)
- [Routing tests](../../../Test/Infra/Ai/Providers/AnalysisProviderSelectionTests.cs)
- [Azure setup](../../../docs/azure-openai-setup.md)
