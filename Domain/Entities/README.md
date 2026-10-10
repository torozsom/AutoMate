# Entities

`AiAnalysisBudgetEntry` stores owner-account admission/attempt accounting independently of project/deployment/result
lifetime. It holds analysis/lease IDs, kind, accounting day/time and conservative integer monetary reservation/currency.
Only account deletion cascades. It contains no diagnostic payload or provider response. Ninety-day bounded retention
is implemented in Infrastructure; these reservations do not assert provider billing or actual model cost.

Persisted business entities with no EF Core or transport dependencies.

## Source inventory

- `Application.cs`
- `AzureContainerAppLogCheckpoint.cs`
- `BaseEntity.cs`
- `Configuration.cs`
- `CsProject.cs`
- `CloudDeploymentRun.cs`, `CloudRunOutbox.cs`, `CloudWebhookDelivery.cs`, `CloudInstallationBudget.cs` — SaaS
  control-plane state, verified webhook inbox, and installation cooldowns.
- `CloudDeploymentRun.cs`, `CloudRunOutbox.cs`, `CloudWebhookDelivery.cs`, `CloudInstallationBudget.cs` — SaaS
  control-plane state, verified webhook inbox, and installation cooldowns.
- `Deployment.cs`
- `GitHubWorkflowCheckpoint.cs`
- `GitHubWorkflowJobCheckpoint.cs`
- `LocalUser.cs`
- `RemoteUser.cs`
- `User.cs`

## Boundary

Keep this module independent of Application, Infrastructure, Web, framework APIs, and provider SDKs.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)

`TelemetryTenantState.cs` owns provider-neutral leases, rate windows and bounded loss/series state. Application runtime
and managed-egress preferences are explicit default-off consent fields.

`AiDeploymentAnalysis` stores validated/redacted analysis guidance with optional requested/returned model provenance,
explicit model revision, prompt/result schema versions, token counts and decimal cost/currency metadata. Legacy optional
metadata remains null. It holds analysis results, not new diagnostic log or metric payloads.

DeploymentAnalysisWorkItem stores analysis identity, latest claim time, nullable lease token/expiry, acquisition count
and terminal completion time. It contains no diagnostic context, log payloads or metric samples.

DeploymentAnalysisWorkItem additionally stores ProviderRetryCount and nullable NextAttemptAt. A scheduled retry resets
AttemptCount for its separately bounded interruption recovery. These are queue scheduling metadata, never payloads.

Owner cancellation preserves analysis identity/timestamps, marks its state Cancelled and atomically retires unfinished
work by completing it and clearing lease/future retry metadata. Deployment state and diagnostic history are unchanged.

AiAnalysisRequest holds metadata-only admission receipts: project/deployment/analysis GUIDs, bounded request key, UTC
admission day, ConsumesQuota and ninety-day expiry. Only Project has a cascading foreign key. Result/deployment deletion
therefore preserves quota/idempotency history; project deletion removes it. Context, result text and payloads are
excluded.

FailedDeploymentAnalysisEvent stores only DeploymentId (primary key/FK), CreatedAt and nullable CompletedAt. Database
capture creates at most one marker per deployment; completion survives result/receipt retention and deletion. Deployment
deletion cascades the marker. It contains neither diagnostic context nor log/metric samples.

## Deployment history update (2026-10-08)

Deployment records own non-secret configuration snapshots, resolved artifacts, an outcome independent of runtime status,
and completion time. Legacy snapshots remain absent. DeploymentArchiveCleanup is a durable deletion outbox without
foreign keys to deleted owners.
