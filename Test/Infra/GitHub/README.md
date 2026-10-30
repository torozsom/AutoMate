# GitHub adapter tests

Signed ingress, secure completed-log downloads, bounded normalization/checkpoints and marked-commit recovery.

`GitHubContractTests.cs` protects secret/dispatch wire names, workflow timing/commit metadata,
private-repository projections, federation issuer and numeric GitHub App configuration binding.

## Contents

| File                                                                           | Test or fixture types                                                                                      | Responsibility                                                                      |
|--------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------|
| [GitHubSaasCommitRecoveryTests.cs](GitHubSaasCommitRecoveryTests.cs)           | `GitHubSaasCommitRecoveryTests`                                                                            | Checks safe remote-commit recovery and provider cooldown detection.                 |
| [GitHubServiceWorkflowLogTests.cs](GitHubServiceWorkflowLogTests.cs)           | `GitHubServiceWorkflowLogTests`, `NoOpDistributedCache`                                                    | Checks secure GitHub Actions job-log download and decoding behavior.                |
| [GitHubWebhookReceiverTests.cs](GitHubWebhookReceiverTests.cs)                 | `GitHubWebhookReceiverTests`                                                                               | Checks signed delivery acceptance, deduplication, and bounded metadata persistence. |
| [GitHubWorkflowCheckpointStoreTests.cs](GitHubWorkflowCheckpointStoreTests.cs) | `GitHubWorkflowCheckpointStoreTests`                                                                       | Verifies durable GitHub Actions workflow and job checkpoints.                       |
| [GitHubWorkflowLogNormalizerTests.cs](GitHubWorkflowLogNormalizerTests.cs)     | `GitHubWorkflowLogNormalizerTests`                                                                         | Verifies safe normalization and bounded paging of GitHub Actions log text.          |
| [GitHubWorkflowMonitorTests.cs](GitHubWorkflowMonitorTests.cs)                 | `GitHubWorkflowMonitorTests`, `RecordingPublisher`, `PassThroughRedactor`, `WorkflowSequenceGitHubService` | Verifies that workflow progress precedes completed-run log output.                  |

## Coverage and navigation

WorkflowLog tests verify signed redirects do not receive OAuth credentials and ZIP content is decoded before
presentation. LogNormalizer/CheckpointStore tests check line/chunk paging and durable job reuse. WorkflowMonitor
verifies changed progress precedes completed logs. WebhookReceiver verifies signatures/delivery deduplication. SaaS
commit recovery checks prior marked SHA and throttle-reset handling.

## Fixtures and boundaries

All provider responses/keys/signatures are synthetic; relational fixtures are owned locally. No GitHub repository
mutation or live token is needed. Keep raw log payloads out of checkpoint state, preserve workflow-attempt identity and
distinguish transport/diagnostic failure from remote workflow conclusion.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra.GitHub
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Integration map](../../../Infrastructure/GitHub/README.md)
- [Workflow consumer](../../../Infrastructure/ApplicationServices/Orchestration/Lifecycle/README.md)
- [Projection contracts](../../../Domain/DTO/GitHub/README.md)
