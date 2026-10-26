# Assessment context contracts

Ephemeral redacted analysis input, assessment preferences/provenance and focused archive evidence queries.

## Contents

| File                                                                         | Types                                                                                                                 | Responsibility                                                                                             |
|------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------|
| [AssessmentDetails.cs](AssessmentDetails.cs)                                 | `AssessmentSections`, `AssessmentPreferences`, `AssessmentChannel`, `ArchiveAssessmentQuery`, `ArchiveAssessmentPage` | Structured answer sections, assessment preferences/channels and bounded archive evidence requests/results. |
| [IDeploymentAnalysisContextBuilder.cs](IDeploymentAnalysisContextBuilder.cs) | `IDeploymentAnalysisContextBuilder`, `DeploymentAnalysisContext`                                                      | Builds redacted, bounded, in-memory evidence context without persisting diagnostic snapshots.              |

## Usage and integration

IDeploymentAnalysisContextBuilder produces DeploymentAnalysisContext with Text, selected EvidenceReferences and optional
provenance. Infrastructure's DeploymentAnalysisContextBuilder and AssessmentEvidenceReader gather evidence using
Application selection/budget policy. AssessmentDetails defines UI preferences, channels, structured answer sections and
archive query/page projections.

## Contracts and lifecycle

Context text and the evidence catalog remain in memory and are not queued or saved as new diagnostic snapshots.
AssessmentPreferences distinguishes available channels/metric containers and unsupported source categories.
ArchiveAssessmentQuery carries tenant/project/deployment scope, normalized selection, frozen window, limit and optional
catalog-only mode.

The default selection-aware builder overload delegates to the legacy overload and ignores selection. Implementers
supporting user-selected evidence must override it. AssessmentSections contains authored observations, metric
assessment, potential issues and limitations; it remains untrusted until result validation. Records/readonly interfaces
do not guarantee their arrays or backing collections are immutable.

## Related documentation

- [Parent module](../README.md)
- [Context implementation](../../../../Infrastructure/Ai/Context/DeploymentAnalysisContextBuilder.cs)
- [Evidence reader](../../../../Infrastructure/Ai/Context/AssessmentEvidenceReader.cs)
- [Selection and budgets](../../../Ai/Context/README.md)
- [Archive capabilities](../../Diagnostics/History/README.md)
- [Selection tests](../../../../Test/Infra/Ai/ContextAndResults/AssessmentSelectionTests.cs)
- [Assessment architecture](../../../../docs/status-aware-assessments.md)
