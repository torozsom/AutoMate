# Analysis result schema

Diagnostic/analysis baseline, optional result provenance and status-aware structured assessments.

## Contents

| File                                                                                                                                 | Types or asset                          | Responsibility                                                                                |
|--------------------------------------------------------------------------------------------------------------------------------------|-----------------------------------------|-----------------------------------------------------------------------------------------------|
| [20261002062913_AddDeploymentDiagnosticsAndAiAnalysis.cs](20261002062913_AddDeploymentDiagnosticsAndAiAnalysis.cs)                   | `AddDeploymentDiagnosticsAndAiAnalysis` | EF schema migration and target-model metadata for Add Deployment Diagnostics And Ai Analysis. |
| [20261002062913_AddDeploymentDiagnosticsAndAiAnalysis.Designer.cs](20261002062913_AddDeploymentDiagnosticsAndAiAnalysis.Designer.cs) | `AddDeploymentDiagnosticsAndAiAnalysis` | EF schema migration and target-model metadata for Add Deployment Diagnostics And Ai Analysis. |
| [20261004201350_AddAiAnalysisResultProvenance.cs](20261004201350_AddAiAnalysisResultProvenance.cs)                                   | `AddAiAnalysisResultProvenance`         | EF schema migration and target-model metadata for Add Ai Analysis Result Provenance.          |
| [20261004201350_AddAiAnalysisResultProvenance.Designer.cs](20261004201350_AddAiAnalysisResultProvenance.Designer.cs)                 | `AddAiAnalysisResultProvenance`         | EF schema migration and target-model metadata for Add Ai Analysis Result Provenance.          |
| [20261009091530_StatusAwareAssessments.cs](20261009091530_StatusAwareAssessments.cs)                                                 | `StatusAwareAssessments`                | EF schema migration and target-model metadata for Status Aware Assessments.                   |
| [20261009091530_StatusAwareAssessments.Designer.cs](20261009091530_StatusAwareAssessments.Designer.cs)                               | `StatusAwareAssessments`                | EF schema migration and target-model metadata for Status Aware Assessments.                   |

## Usage and integration

AddDeploymentDiagnosticsAndAiAnalysis creates the initial diagnostic/analysis/work metadata and consent field.
AddAiAnalysisResultProvenance adds nullable provider/model/prompt/schema/usage/cost fields. StatusAwareAssessments adds
selection/provenance/section JSON and deployment preferences. Designers record each target model.

## Contracts and lifecycle

These files are schema history, not authorization to write new diagnostic payloads through legacy tables. Current
execution keeps context in memory and saves only validated/redacted result/selection metadata. Optional provenance is
not backfilled with invented usage/model versions. Cost uses numeric (18,8); do not infer invoice data from optional
estimates.

Preserve serialized compatibility, migration IDs and designer pairs. Follow later retention migrations for saved-result
lifetime; a result's execution deadline does not alone define deletion.

## Related documentation

- [Parent module](../README.md)
- [Result management](../../Ai/Processing/README.md)
- [Validation](../../../Application/Ai/Results/README.md)
- [Metadata](../../../Domain/Entities/Ai/README.md)
- [Persistence tests](../../../Test/Infra/Ai/ContextAndResults/AnalysisPersistenceTests.cs)
- [Assessment contracts](../../../docs/status-aware-assessments.md)
