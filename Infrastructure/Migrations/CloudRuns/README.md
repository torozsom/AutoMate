# SaaS cloud execution schema

Durable run/outbox/webhook metadata, installation cooldowns and shared-admission indexes.

## Contents

| File                                                                                                           | Types or asset               | Responsibility                                                                   |
|----------------------------------------------------------------------------------------------------------------|------------------------------|----------------------------------------------------------------------------------|
| [20261003101739_AddSaasCloudRuns.cs](20261003101739_AddSaasCloudRuns.cs)                                       | `AddSaasCloudRuns`           | EF schema migration and target-model metadata for Add Saas Cloud Runs.           |
| [20261003101739_AddSaasCloudRuns.Designer.cs](20261003101739_AddSaasCloudRuns.Designer.cs)                     | `AddSaasCloudRuns`           | EF schema migration and target-model metadata for Add Saas Cloud Runs.           |
| [20261003103139_AddCloudInstallationBudget.cs](20261003103139_AddCloudInstallationBudget.cs)                   | `AddCloudInstallationBudget` | EF schema migration and target-model metadata for Add Cloud Installation Budget. |
| [20261003103139_AddCloudInstallationBudget.Designer.cs](20261003103139_AddCloudInstallationBudget.Designer.cs) | `AddCloudInstallationBudget` | EF schema migration and target-model metadata for Add Cloud Installation Budget. |
| [20261003105303_AddCloudWebhookBranch.cs](20261003105303_AddCloudWebhookBranch.cs)                             | `AddCloudWebhookBranch`      | EF schema migration and target-model metadata for Add Cloud Webhook Branch.      |
| [20261003105303_AddCloudWebhookBranch.Designer.cs](20261003105303_AddCloudWebhookBranch.Designer.cs)           | `AddCloudWebhookBranch`      | EF schema migration and target-model metadata for Add Cloud Webhook Branch.      |
| [20261003105945_AddCloudAdmissionIndexes.cs](20261003105945_AddCloudAdmissionIndexes.cs)                       | `AddCloudAdmissionIndexes`   | EF schema migration and target-model metadata for Add Cloud Admission Indexes.   |
| [20261003105945_AddCloudAdmissionIndexes.Designer.cs](20261003105945_AddCloudAdmissionIndexes.Designer.cs)     | `AddCloudAdmissionIndexes`   | EF schema migration and target-model metadata for Add Cloud Admission Indexes.   |

## Usage and integration

AddSaasCloudRuns establishes runs, transactional wakeups and verified receipts. AddCloudInstallationBudget shares
provider-throttle cooldown. AddCloudWebhookBranch retains branch correlation. AddCloudAdmissionIndexes supports
active/queued/owner/repository scheduling queries. Each migration has its target-model designer.

## Contracts and lifecycle

Run snapshot protection is configured in runtime mappings and requires the intended shared keys; schema alone does not
protect in-memory inputs. Queue no provider tokens/raw webhook bodies. Correlation and uniqueness must survive
retries/replicas without treating a latest unrelated workflow as a match.

EF migration IDs determine chronological execution independently of folders. Preserve string CloudRunPhase
compatibility, index/foreign-key semantics and designer metadata. Deleting transient receipts/outbox is separate from
deployment archive retention.

## Related documentation

- [Parent module](../README.md)
- [Execution consumers](../../ApplicationServices/Orchestration/CloudRuns/README.md)
- [Protection/mappings](../../Data/README.md)
- [Models](../../../Domain/Entities/Cloud/README.md)
- [Run tests](../../../Test/Infra/Orchestration/CloudDeploymentRunServiceTests.cs)
- [Operations](../../../docs/saas-cloud-deployments.md)
