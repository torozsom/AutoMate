# GitHub projections

Repository import metadata and workflow/job/log projections used by GitHub-facing ports.

## Contents

| File                                               | Types and responsibility                                                                                                                                                                          |
|----------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| [GitHubRepositoryDto.cs](GitHubRepositoryDto.cs)   | GitHubRepositoryDto: import metadata with explicit GitHub JSON property names.                                                                                                                    |
| [GitHubWorkflowRunDto.cs](GitHubWorkflowRunDto.cs) | GitHubWorkflowRunDto: run/attempt identity, status, conclusion and commit/branch correlation.                                                                                                     |
| [GitHubWorkflowJobDto.cs](GitHubWorkflowJobDto.cs) | GitHubWorkflowJobDto and GitHubWorkflowStepDto: ordered progress; GitHubWorkflowJobLogDownload: availability plus optional content; GitHubWorkflowLogArchiveEntryDto: final archive path/content. |

## Usage and integration

[IGitHubService](../../../Application/Abstractions/GitHub/IGitHubService.cs) exposes retrieval operations implemented
by [GitHubService](../../../Infrastructure/GitHub/Api/GitHubService.cs). Provider responses are mapped into these
projections. [GitHubWorkflowMonitor](../../../Infrastructure/ApplicationServices/Orchestration/Lifecycle/GitHubWorkflowMonitor.cs)
consumes progress and log results.

## Contracts and lifecycle

Workflow attempts default to 1. Status and conclusion retain provider strings; conclusion is nullable until known. Job
steps preserve ordered progress. Repository JSON names are part of provider deserialization compatibility.

GitHubWorkflowJobLogDownload separates retrieval availability from content. Downloaded logs and archive entries are
untrusted output requiring downstream normalization and redaction. AccessDenied or NotAvailable describes log retrieval,
not deployment outcome. DTOs do not reconcile final output or advance checkpoints themselves.

## Related documentation

- [DTO index](../README.md)
- [Log availability](../../Enums/GitHub/README.md)
- [Streaming checkpoints](../../Entities/Diagnostics/README.md)
- [Log retrieval tests](../../../Test/Infra/GitHub/GitHubServiceWorkflowLogTests.cs)
- [Monitor tests](../../../Test/Infra/GitHub/GitHubWorkflowMonitorTests.cs)
