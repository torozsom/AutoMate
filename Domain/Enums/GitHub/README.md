# GitHub log availability

Finite outcomes for retrieving a GitHub Actions job log, independent of workflow success or failure.

## Contents

| File                                                                 | Types and responsibility                                                                 |
|----------------------------------------------------------------------|------------------------------------------------------------------------------------------|
| [GitHubWorkflowLogAvailability.cs](GitHubWorkflowLogAvailability.cs) | GitHubWorkflowLogAvailability: Unknown, Available, NotAvailable, AccessDenied or Failed. |

## Usage and integration

Returned in [GitHubWorkflowJobLogDownload](../../DTO/GitHub/GitHubWorkflowJobDto.cs) and retained
in [job checkpoints](../../Entities/Diagnostics/GitHubWorkflowJobCheckpoint.cs). [GitHubWorkflowLogReader](../../../Infrastructure/GitHub/Workflows/GitHubWorkflowLogReader.cs)
classifies retrieval outcomes.

## Contracts and compatibility

Unknown means no attempt; Available means a downloadable log was returned; NotAvailable means GitHub has not made it
available; AccessDenied denotes insufficient permission; Failed covers other retrieval failures. Availability alone does
not establish whether publication or final archive reconciliation has completed. Preserve order/values when extending
persisted checkpoint state.

## Related documentation

- [Enums index](../README.md)
- [GitHub projections](../../DTO/GitHub/README.md)
- [Retrieval classification tests](../../../Test/Infra/GitHub/GitHubServiceWorkflowLogTests.cs)
