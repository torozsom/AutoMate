# Workflow projections and checkpoints

Provider response mapping, bounded completed-log parsing and durable non-sensitive progress.

## Contents

| File                                                                 | Types or asset                                                                  | Responsibility                                                                        |
|----------------------------------------------------------------------|---------------------------------------------------------------------------------|---------------------------------------------------------------------------------------|
| [GitHubWorkflowCheckpointStore.cs](GitHubWorkflowCheckpointStore.cs) | `GitHubWorkflowCheckpointStore`                                                 | Persists non-sensitive workflow streaming checkpoints for restart-safe deduplication. |
| [GitHubWorkflowJobMapper.cs](GitHubWorkflowJobMapper.cs)             | `GitHubWorkflowJobMapper`                                                       | Maps GitHub REST job payloads into Application-facing DTOs.                           |
| [GitHubWorkflowJobModels.cs](GitHubWorkflowJobModels.cs)             | `GitHubWorkflowJobsResponse`, `GitHubWorkflowJobItem`, `GitHubWorkflowStepItem` | GitHub REST response for a workflow run's jobs.                                       |
| [GitHubWorkflowLogNormalizer.cs](GitHubWorkflowLogNormalizer.cs)     | `GitHubWorkflowLogNormalizer`                                                   | Normalizes untrusted GitHub log text into bounded terminal-safe lines.                |
| [GitHubWorkflowLogReader.cs](GitHubWorkflowLogReader.cs)             | `GitHubWorkflowLogReader`                                                       | Reads GitHub Actions job logs and named workflow archive entries.                     |
| [GitHubWorkflowRunMapper.cs](GitHubWorkflowRunMapper.cs)             | `GitHubWorkflowRunMapper`                                                       | Selects and maps GitHub workflow run API items into AutoMate DTOs.                    |
| [GitHubWorkflowRunModels.cs](GitHubWorkflowRunModels.cs)             | `GitHubWorkflowRunsResponse`, `GitHubWorkflowRunItem`                           | Response shape returned by GitHub when listing workflow runs.                         |

## Usage and integration

RunMapper selects matching workflow projections; JobMapper normalizes jobs/ordered steps from wire models. LogReader
handles downloaded archive entries; LogNormalizer bounds terminal-safe lines/chunks and hashes normalized prefixes.
CheckpointStore loads/creates workflow-attempt/job progress. Lifecycle GitHubWorkflowMonitor owns
progress-then-completed-logs orchestration.

## Contracts and lifecycle

Publish changed job/step state while running, then process job downloads/final archives after workflow completion.
Pending steps do not imply log probes. Treat all archive names/content as untrusted; normalization does not replace
central redaction.

Normalizer caps lines at 16 KiB and chunks at 512 KiB. Persist only fingerprints, counts, prefix/content/archive hashes
and availability/finality, never log payloads. Workflow ID plus attempt scopes reruns; confirm durable output before
advancing progress. Delivery failure is separate from remote workflow outcome.

## Related documentation

- [Parent module](../README.md)
- [Monitor consumer](../../ApplicationServices/Orchestration/Lifecycle/README.md)
- [Shared DTOs](../../../Domain/DTO/GitHub/README.md)
- [Cursor models](../../../Domain/Entities/Diagnostics/README.md)
- [Normalization tests](../../../Test/Infra/GitHub/GitHubWorkflowLogNormalizerTests.cs)
- [Cursor tests](../../../Test/Infra/GitHub/GitHubWorkflowCheckpointStoreTests.cs)
- [Ordering tests](../../../Test/Infra/GitHub/GitHubWorkflowMonitorTests.cs)
