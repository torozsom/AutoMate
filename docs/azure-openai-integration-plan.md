# Azure OpenAI integration record

Prepared 2026-10-05; API-key adapter implemented 2026-10-06 following the requested Web user-secrets flow.
Both hosting profiles register `azure-openai` alongside direct `openai`. No Azure resource, credential, model
deployment,
paid request, data-processing approval, project consent or application database migration was created here.
Feature/automatic/egress flags and USD spending defaults remain off.

## Implemented API-key path

Follow [Azure Portal and Web user-secrets setup](azure-openai-setup.md) to configure the resource and manual pilot.
Infrastructure owns `AzureOpenAiAnalysisProvider` and typed `AzureOpenAiOptions`. Exact resource-name matching allows
only `https://<resource>.openai.azure.com/openai/v1/`, with `AiAnalysis:Model` identifying the deployed model alias.
The separate `AiAnalysis:AzureOpenAi:ApiKey` supplies the Azure REST `api-key` header; direct OpenAI keeps its own
credential and route policy. No implicit authentication or provider fallback exists.

`ResponsesAnalysisTransport` shares tested bounded POST transport, strict schema, untrusted-data instructions,
`store=false`, no tools, response validation/redaction, evidence membership and safe provenance. Redirects and inline
HTTP retries remain disabled. Azure numeric `429` error codes require a Retry-After hint for transient classification;
unknown/quota envelopes are terminal. Common worker budgets reserve before every attempt. Nested key/resource reload
invalidates the enclosing AI snapshot, cancels local headers/body I/O and prevents stale publication/retry. Credentials
are header-safe and never included in results/telemetry; Azure factory HTTP loggers are removed.

The Application ports, worker, durable queue/leases/receipts/retries, context builder, validator, owner-account budget
boundary, consent and UI are reused unchanged. Readiness checks local route/key presence and metadata only; it never
acquires a credential or calls Azure. No NuGet dependency or metadata migration was added.

## Verified API baseline

Microsoft documents Responses at `https://<resource>.openai.azure.com/openai/v1/responses`, with `model` identifying the
deployed model. REST API-key authentication uses `api-key`. Verify model/version, strict-schema and region availability.
[Microsoft Responses guide](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/responses).

Resource location alone does not establish inference residency: Global deployments can process across geographies,
and DataZone processing follows its specified zone. Review deployment type, monitoring, storage/retention and contract.
[Microsoft data/privacy guidance](https://learn.microsoft.com/en-us/azure/foundry/responsible-ai/openai/data-privacy).
The current common policy retains approved US/EU processing; flags attest to a real operator decision rather than
proving geography. Managed identity is a future extension and was intentionally excluded from the requested key flow.

## Credential-free acceptance

- [x] Register/select both providers in SelfHosted and SaaS with default-off startup and exact resource checks.
- [x] Synthetic HTTP verifies Azure v1 path, API-key header, deployment alias, strict schema and provider provenance.
- [x] Run the shared Responses contract against both adapters: malformed/oversized/refused/incomplete responses,
  context/result redaction and bounds, evidence rejection, safe failures, timeout/cancellation and no inline retry.
- [x] Reject missing/unsafe Azure keys, unrelated hosts, credentials/ports/query/fragment and resource mismatch before
  transmission; never fall back to direct credentials/providers.
- [x] Exercise real nested key/resource reload during headers/body I/O and deny subsequent calls without retry.
- [x] Run broad .NET/JavaScript regressions and both-hosting-profile catalog/credential checks; update navigation,
  onboarding/configuration/module documentation and ADR.
- [x] Run six real migration/trigger/admission/reservation checks on the owned disposable PostgreSQL runner.

Final verification: 663 .NET tests passed with sixteen opt-in external-stack skips, six disposable PostgreSQL checks
passed separately, and six JavaScript tests passed. Solution compilation and scoped whitespace verification succeeded.
The initial restricted Windows Web run failed on sandbox Event Log/data-protection access; the permitted full run
passed.

Shared owner/cohort/consent/geography/category/spend/capacity tests remain in the existing suite. Production host
network/RBAC/key validity, actual model envelopes/content filters and provider quality require live acceptance.

## Environment inputs required before an Azure call

| Input                 | Required evidence / decision                                                                   |
|-----------------------|------------------------------------------------------------------------------------------------|
| Resource and endpoint | Approved resource identity, exact v1 URL and reachable network/private DNS.                    |
| Model deployment      | Alias, model/version, Responses/strict-schema support and bounded input/output settings.       |
| Placement             | Region, deployment type, approved processing geography and data-control contract review.       |
| Authentication        | Protected Azure resource key and key authentication enabled on that resource.                  |
| Pilot                 | Approved internal owner accounts, consenting anonymized sample projects and reviewer criteria. |
| Spending              | Accurate whole-request USD bound, positive daily allowance and provider-side controls.         |

Use the [pilot record](ai-analysis-rollout.md)
and [shutdown controls](ai-analysis-operations.md#staged-feature-enablement-and-shutdown).
No live pilot, model quality, exactly-once remote execution/billing or production acceptance is claimed.
