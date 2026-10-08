# Azure OpenAI setup for local AI analysis

The Azure API-key adapter is implemented in both SelfHosted and SaaS. No more application code or SDK installation is
needed. You must supply your resource endpoint, deployment alias, API key and the existing pilot policy/budget settings.
No Azure resource, credential, paid request, project consent or live database change was created by implementation.
Managed identity is a future extension; this adapter deliberately supports only the requested API-key flow.

## 1. Create the Azure resource

1. Sign in to [Azure Portal](https://portal.azure.com). Choose **Create a resource**, search for **Azure OpenAI**, and
   select **Create**. Choose your subscription, resource group, resource name and an available region supporting your
   intended model and Responses API. Use the Standard resource pricing tier offered by the portal.
2. For an EU pilot, select an EU resource location and verify the model's actual deployment type/processing geography.
   Standard resource pricing tier and model deployment type are separate choices. Global inference can process outside
   the resource geography; do not assume an EU resource makes a Global deployment EU-only. The app's current policy
   supports approved `eu` or `us` processing only.
3. Configure networking so your development machine can reach the resource. Selected-network rules allowing your
   outbound public IP are suitable where available; private endpoints require a working VPN/DNS route. Keep resource
   key authentication enabled for this adapter.
4. Review and create the resource, then select **Go to resource**.

See
Microsoft's [resource creation guide](https://learn.microsoft.com/en-us/azure/foundry-classic/openai/how-to/create-resource?pivots=web-portal),
[Responses model/region support](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/responses) and
[processing-location guidance](https://learn.microsoft.com/en-us/azure/foundry/responsible-ai/openai/data-privacy).

## 2. Deploy a model

1. Open the resource in [Microsoft Foundry](https://ai.azure.com) using the portal's **Model deployments** / **Manage
   deployments** link. Microsoft's classic instructions use the **New Foundry** toggle off, then **View all resources**,
   your resource, and **Deployments**. An upgraded Foundry resource may label this **Models + endpoints**.
2. Choose **Deploy model → Deploy base model**. Select a text model supporting both Responses and strict structured
   outputs. `gpt-4.1-mini` is one documented compatible starting option if it is available in your subscription/region;
   choose another compatible model when appropriate. This is not a measured quality recommendation for AutoMate.
3. Name the deployment, for example `automate-analysis`. This exact alias goes in `AiAnalysis:Model`, even when the
   underlying model has a different name. Use an alias of at most 100 characters with letters, digits, `-`, `_`, `.`,
   `:`
   so it also fits stored provenance.
4. Select an available deployment type matching your approved geography, allocate enough tokens-per-minute capacity
   for the bounded request, and deploy. Wait for provisioning to succeed. Batch-only deployments are unsuitable here.
5. Review the selected model/version/deployment's actual pricing and Azure usage controls before enabling spending.

See [deployment steps](https://learn.microsoft.com/en-us/azure/foundry-classic/openai/how-to/create-resource?pivots=web-portal#deploy-a-model)
and [structured-output support](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/structured-outputs).

## 3. Copy the endpoint and resource key

In the Azure Portal resource, open **Resource Management → Keys and Endpoint**, then **Show Keys**. Copy **KEY 1** (or
KEY 2) and the endpoint. This is the Azure resource key, not an OpenAI-platform key or an Azure OAuth token.
Microsoft's [portal walkthrough](https://github.com/microsoft/generative-ai-for-beginners/blob/main/00-course-setup/03-providers.md#configure-azure-openai-from-portal)
describes these fields.

For an endpoint `https://my-resource.openai.azure.com/`, configure:

- `AzureOpenAi:ResourceName`: `my-resource` (the exact lowercase DNS prefix shown in the endpoint).
- `Endpoint`: `https://my-resource.openai.azure.com/openai/v1/` (including the trailing slash).
- `Model`: your deployment alias, such as `automate-analysis`.

The adapter accepts this exact resource route only. Do not use a Foundry project `/api/projects/...` URL, the full
`/responses` operation URL, a proxy, an API-version query string or a `services.ai.azure.com` hostname. Private DNS may
resolve the same canonical resource hostname privately; network setup remains yours.

## 4. Merge settings into Web user-secrets

Web already has `UserSecretsId=automate-web-local-development`. In Visual Studio, right-click **Web → Manage User
Secrets**. In other IDEs, open the corresponding local secrets JSON. On this Windows development account the standard
location is `%APPDATA%\Microsoft\UserSecrets\automate-web-local-development\secrets.json`. Preserve existing database,
OAuth and telemetry settings. Add or merge one `AiAnalysis` object; do not create duplicate JSON keys.

This template intentionally stays disabled and uses zero spending until you finish the values and review approvals:

```json
"AiAnalysis": {
  "Enabled": false,
  "AutomaticAnalysisEnabled": false,
  "ProviderEgressEnabled": false,
  "Provider": "azure-openai",
  "Endpoint": "https://my-resource.openai.azure.com/openai/v1/",
  "Model": "automate-analysis",
  "AzureOpenAi": {
    "ResourceName": "my-resource",
    "ApiKey": "PASTE-YOUR-AZURE-RESOURCE-KEY-HERE"
  },
  "RegionalProcessingApproved": false,
  "ProcessingRegion": "eu",
  "ApprovedRegions": ["eu"],
  "ApprovedTenantIds": ["REPLACE-WITH-YOUR-AUTOMATE-OWNER-GUID"],
  "AllowedDataCategories": ["logs", "metrics", "traceCorrelation"],
  "MaximumOutputTokens": 4096,
  "MaximumProviderRetries": 0,
  "DailyProjectLimit": 5,
  "BudgetCurrency": "USD",
  "DailyTenantCostBudget": 0,
  "MaximumProviderAttemptCost": 0
}
```

`ApprovedTenantIds` contains your internal AutoMate account GUID, not the Azure directory/tenant/subscription ID or a
GitHub numeric ID. Obtain the account's `id` from AutoMate's `users` metadata table through your existing database tool.
For example, locate your own account with `SELECT id, email FROM users WHERE email = '<your account email>';`.
All projects for that owner share tenant limits; each project still requires its own consent.

You can also use the CLI, from the repository root:

```powershell
dotnet user-secrets set "AiAnalysis:AzureOpenAi:ApiKey" "<your Azure resource key>" --project Web
```

The command example is a placeholder. Prefer the IDE editor for the actual key to avoid putting it into shell history.
`AZURE_OPENAI_API_KEY` alone is not read by this adapter. The deployment environment equivalent is
`AiAnalysis__AzureOpenAi__ApiKey`. Never put an actual key in tracked appsettings or this documentation. User-secrets
are
local development configuration, not an encrypted production vault.

## 5. Configure spending and enable your manual pilot

Set `MaximumProviderAttemptCost` to a positive conservative USD bound for the whole request, and
`DailyTenantCostBudget` to your positive daily allowance (at least that attempt bound). Money accepts up to eight
decimal
places. Zero intentionally blocks every provider attempt. Calculate the bound from the selected deployment's current
uncached input/output prices, the conservative context limit, fixed instructions/schema overhead and output allowance:

```text
attempt bound >= (maximum total input tokens * input USD per million
                + maximum output tokens * output USD per million) / 1,000,000
```

The default conservative context allowance is 12,000 token units; it excludes fixed instructions/schema overhead.
The template requests at most 4,096 output tokens, including reasoning where applicable. Include overhead and a
conservative margin in your calculation; do not assume context characters are billable tokens. Review
[Azure pricing](https://azure.microsoft.com/en-us/pricing/details/cognitive-services/openai-service/) for your actual
deployment. AutoMate reserves the attempt bound before each retry/recovery without refund. Azure budget alerts alone
are notifications, not a guaranteed hard spend stop; review actual usage and provider controls. See
[budget semantics](../Infrastructure/Ai/README.md#shared-tenant-limits-and-reserved-spend-m8).

After confirming the actual provider/data-processing/geography approvals, set `RegionalProcessingApproved=true`,
`Enabled=true` and `ProviderEgressEnabled=true`. Keep `AutomaticAnalysisEnabled=false` for the first trial. Setting an
approval flag records your decision; it does not establish Azure residency or grant a contract.

## 6. Start AutoMate and request analysis

1. Ensure the existing PostgreSQL, Redis and disk-gateway/Loki/Mimir development dependencies are running; see
   [local startup](local-development.md). Stop old Web/worker binaries before applying any pending metadata migrations.
2. From the repository root, build and update your intended development database using the existing workflow:

   ```powershell
   dotnet build AutoMate.slnx
   dotnet ef database update --project Infrastructure --startup-project Web --no-build
   dotnet run --project Web --launch-profile https --no-build
   ```

   The adapter itself adds no migration; all existing AI queue/budget migrations must be installed. The `https` launch
   profile runs in Development and loads Web user-secrets. Published/container hosts need protected deployment settings.
3. Visit `https://localhost:7288/health/ready`. `Ready/Available` confirms local configuration/metadata, not that the
   key,
   model, network or quota works remotely. Health probes never spend tokens.
4. Sign in as the approved AutoMate owner. Open a failed deployment with collected diagnostics. In **AI analysis**,
   click **Allow diagnostic data egress**, then **Analyze**. Begin with an anonymized sample. Results refresh every five
   seconds
   and display `azure-openai` provenance. Empty diagnostic context is skipped without a call.
5. Review summary, steps, evidence, latency and Azure usage. No suggested remediation is executed automatically.
   Follow the [pilot record](ai-analysis-rollout.md) before enabling automatic analysis or widening the cohort.

## Troubleshooting and shutdown

- Startup configuration failure: check exact resource name/endpoint, deployment alias, owner GUID, categories and
  regional approval. Error messages deliberately omit secret values.
- Skipped/unavailable: check the Azure-specific key, feature/egress flags, cohort, current project consent and
  readiness.
- Skipped/budget_exceeded: account reservations exhausted the allowance; wait for midnight UTC. Lowering future attempt
  bounds does not refund existing reservations.
- Skipped/budget_not_configured or budget_configuration_invalid: correct the configured amounts/shared limits.
- Skipped/budget_currency_mismatch: restore the reservation currency or wait for midnight UTC.
- Failed/provider_failure: verify key/resource pairing, networking, deployment existence, Responses/strict-schema
  support
  and Azure's own diagnostics. Raw provider error bodies are not displayed by AutoMate.
- Failed/invalid_response: investigate refusals/content filtering, incomplete output from a low output allowance, or
  unsupported schema behavior. The adapter does not publish partial guidance.
- Rate limits: adjust workload or Azure deployment quota. HTTP requests have no inline retries; the durable worker
  retries only classified transient failures when `MaximumProviderRetries` permits them.

For shutdown set `ProviderEgressEnabled=false` in effective configuration. Standard JSON/user-secrets reload cancels
local active headers/body reads and denies new calls once the process observes it, including key/resource changes.
Environment-variable overrides take precedence and require restart. Verify every replica; already-sent Azure work
cannot be recalled. Full shutdown/retention details are in [operations](ai-analysis-operations.md).

## Spending exhaustion and console diagnosis

Local and cloud deployments share their owner's daily reservation allowance. Setting both DailyTenantCostBudget and
MaximumProviderAttemptCost to 50 USD permits only one provider attempt per UTC day: its full 50 USD is reserved, even
if Azure's actual charge is much smaller. Lowering MaximumProviderAttemptCost changes future reservations only; the
existing 50 USD entry remains exhausted until midnight UTC. Do not delete accounting entries to unblock execution.

For a reviewed model/deployment price, use uncached USD rates per million tokens:
attempt_bound = ceiling_to_cent (2 * ((maximum_context_tokens + fixed_request_token_upper_bound) * input_rate
+ maximum_output_tokens * output_rate) / 1,000,000).
Include the actual fixed instructions, response schema and request framing in the input upper bound. The current pilot
uses gpt-5.4-mini, default 12,000 context byte/token units and 4,096 output tokens including reasoning; retries stay
zero.
Use the rate for the deployment's region/type/tier from the Azure Portal or official Azure pricing, never direct OpenAI
API rates or cached/batch prices. Keep the daily ceiling at 50 USD and update only AiAnalysis:MaximumProviderAttemptCost
in Web user secrets once the bound is verified.

The October 2026 detailed Azure usage file supplied by the operator verifies the current Data Zone Standard (EUR)
gpt-5.4-mini meters: uncached input costs **0.825 USD per million tokens** (`5.4 mini Inp Dz 1M Tokens`), and output
costs **4.95 USD per million tokens** (`5.4 mini Opt Dz 1M Tokens`). UnitPrice, EffectivePrice and payGPrice agree;
pricingCurrency
is USD even though billingCurrency is EUR. The billed quantities reproduce the USD charges exactly: 0.003996 million
input tokens * 0.825 = 0.0032967 USD, and 0.000370 million output tokens * 4.95 = 0.0018315 USD. These are subscription
billing evidence, not prices inferred from a rounded dashboard. The public pricing page/retail API did not expose the
applicable rates during this investigation. Review prices again after a model, deployment type, region or tariff change.

For the current request, the fixed instructions, strict response schema and request fields occupy 1,008 UTF-8 bytes in a
compact JSON representation with empty input. Allow **2,048 input token units** for that fixed content and provider
framing, in addition to the existing 12,000-unit conservative encoded context limit. This deliberately counts bytes as
token units rather than using an average text-to-token ratio. The context limit already includes its diagnostics
wrapper.

```text
maximum input = 12,000 + 2,048 = 14,048 units
maximum output = 4,096 tokens (including reasoning)
base cost = (14,048 * 0.825 + 4,096 * 4.95) / 1,000,000 = 0.0318648 USD
doubled cost = 0.0637296 USD
rounded upward to the next cent = 0.07 USD
```

Web user secrets now set only **AiAnalysis:MaximumProviderAttemptCost = 0.07** (previously 50). The daily ceiling
remains
50 USD, currency remains USD, retries remain zero and existing accounting entries are unchanged. Recalculate the bound
if context/output limits or fixed request content increase. Reload or restart the Web host to use the corrected setting;
published Azure hosts need the corresponding protected deployment configuration because local user secrets are not
published. Lowering this amount does not refund any previous 50 USD reservation.

After the configuration correction, the operator explicitly authorized a one-time reset of all four existing provider
reservations for the pilot account: three 3 USD reservations dated 2026-10-07 and one 50 USD reservation dated
2026-10-08.
Their reserved amounts were set to zero in a single transaction under the provider guard's advisory lock, after checking
that no targeted lease remained active. The four provider-entry identities, dates, currencies and analysis/lease links
were retained; admission entries and analysis results were not modified. This adjustment releases 59 USD of AutoMate
accounting reservations, not Azure charges. It adds no automatic refund behavior: future attempts still reserve 0.07 USD
without refund against the 50 USD daily ceiling. This is an explicitly authorized pilot exception to the normal
immutable
reservation policy, not a routine deployment or refund procedure.

Denials now explain exhaustion, absent/invalid configuration or currency mismatch separately. Reviewed console warnings
record integer monetary units (100,000,000 units = 1 USD) and safe correlation. Deployment and AI exceptions produce
additional redacted console snapshots, capped at 16 KiB; ordinary logs/OTLP exports remain metadata-only. Restart
AutoMate
to use the updated binaries. Previously captured type-only exceptions cannot be reconstructed from output.txt.
