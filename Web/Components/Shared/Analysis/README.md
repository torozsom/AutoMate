# Assessment UI

Scoped assessment choices/consent/request management and text-only presentation of validated answers.

## Contents

| File                                                                     | Types or asset                            | Responsibility                                                                                  |
|--------------------------------------------------------------------------|-------------------------------------------|-------------------------------------------------------------------------------------------------|
| [AssessmentContextControls.razor](AssessmentContextControls.razor)       | `AssessmentContextControls`               | Edits immutable analysis choices for sources, containers, metrics, focus and UTC time bounds.   |
| [AssessmentContextControls.razor.cs](AssessmentContextControls.razor.cs) | `AssessmentContextControls` partial class | Component state, callbacks and lifecycle behavior.                                              |
| [AssessmentResultSection.razor](AssessmentResultSection.razor)           | `AssessmentResultSection`                 | Presents one validated answer section without interpreting its text as markup.                  |
| [DeploymentAnalysisPanel.razor](DeploymentAnalysisPanel.razor)           | `DeploymentAnalysisPanel`                 | Presents saved analysis state, fixed failure guidance and validated answer content.             |
| [DeploymentAnalysisPanel.razor.css](DeploymentAnalysisPanel.razor.css)   | Scoped stylesheet                         | Scoped styling for DeploymentAnalysisPanel.                                                     |
| [DeploymentAnalysisSection.razor](DeploymentAnalysisSection.razor)       | `DeploymentAnalysisSection`               | Coordinates owned analysis choices, consent, requests, cancellation and bounded result polling. |
| [DeploymentAnalysisSection.razor.cs](DeploymentAnalysisSection.razor.cs) | `DeploymentAnalysisSection` partial class | Component state, callbacks and lifecycle behavior.                                              |

## Usage and integration

DeploymentAnalysisSection coordinates owned preferences, stable request IDs, request/cancel/refresh, result paging and
bounded persisted-state polling through IDeploymentAnalysisService. DeploymentAnalysisPanel renders state/guidance and
delegates actions. AssessmentContextControls edits selection; AssessmentResultSection renders one authored section
without interpreting markup.

## Contracts and lifecycle

Resolve owner/project/deployment/C# project scope together and discard stale route/selection results. Operations use
independent scopes and component-owned cancellation; dispose polling on route/owner change. A stable request ID freezes
options across retries until admission resolves; conflicting choices do not silently replay different work.

Consent and feature enablement are distinct from current provider authorization/reservations. Controls freeze during
pending/active requests. Cancel remains separate from deletion; the panel has no delete control although the HTTP API
supports it. ReadOnly/result views must not expose actions for a mismatched deployment.

Use fixed skip/failure guidance and accessible live status. Only the completed answer receives the distinct result
border; choices, consent and run metadata remain outside. Treat provider-authored content as text and unknown
usage/provenance as unknown, not invented values.

## Related documentation

- [Parent module](../README.md)
- [Result/action contracts](../../../../Application/Abstractions/Ai/Execution/README.md)
- [Selection semantics](../../../../Application/Ai/Context/README.md)
- [HTTP mutations](../../../Routes/Endpoints/README.md)
- [Presenter tests](../../../../Test/Web/Analysis/DeploymentAnalysisSectionTests.cs)
- [Safe rendering tests](../../../../Test/Web/Analysis/DeploymentAnalysisPanelTests.cs)
- [Selection tests](../../../../Test/Web/Analysis/AssessmentContextControlsTests.cs)
- [Polling tests](../../../../Test/Web/Analysis/DeploymentAnalysisPollingTests.cs)
