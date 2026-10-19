# Assessment UI and HTTP tests

Shared assessment choices/actions, safe result markup, serialized polling and protected HTTP/probe behavior.

## Contents

| File                                                                     | Test or fixture types                                                          | Responsibility                                                                                                           |
|--------------------------------------------------------------------------|--------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------|
| [AnalysisReadinessTests.cs](AnalysisReadinessTests.cs)                   | `AnalysisReadinessTests`, `ReadinessPort`                                      | Exercises real health middleware, HTTP status codes, liveness isolation and finite readiness JSON.                       |
| [AssessmentContextControlsTests.cs](AssessmentContextControlsTests.cs)   | `AssessmentContextControlsTests`                                               | Exercises the shared context picker and real event callbacks without provider calls.                                     |
| [DeploymentAnalysisActionTests.cs](DeploymentAnalysisActionTests.cs)     | `DeploymentAnalysisActionTests`, `Fixture`                                     | Exercises real owner consent/cancel handlers with controlled Application ports.                                          |
| [DeploymentAnalysisEndpointTests.cs](DeploymentAnalysisEndpointTests.cs) | `DeploymentAnalysisEndpointTests`, `PortProxy`, `FixtureAuthentication`        | Exercises the real DELETE/cancel routes, authorization middleware and antiforgery validator over loopback HTTP.          |
| [DeploymentAnalysisPanelTests.cs](DeploymentAnalysisPanelTests.cs)       | `DeploymentAnalysisPanelTests`, `ConsentPort`, `EnabledOptions`, `RequestPort` | Renders actual analysis markup to verify state guidance, safe text, stale-result exclusion and owner-action eligibility. |
| [DeploymentAnalysisPollingTests.cs](DeploymentAnalysisPollingTests.cs)   | `DeploymentAnalysisPollingTests`, `Port`                                       | Verifies polling serialization through the shared current/history analysis presenter.                                    |
| [DeploymentAnalysisSectionTests.cs](DeploymentAnalysisSectionTests.cs)   | `DeploymentAnalysisSectionTests`, `Port`                                       | Verifies the shared presenter rather than the previous page-specific polling implementation.                             |

## Coverage and navigation

Panel/ContextControls tests render real components and invoke callbacks for state gates, source/container independence,
frozen controls, fixed guidance and text-only v2 answers. Section/Polling/Action tests verify scope/selection lifetimes,
saved consent/cancellation and late-result exclusion. Endpoint tests host production mappings with real
authorization/antiforgery over loopback. Readiness tests host the actual health middleware.

## Fixtures and boundaries

No provider or production database is used. Application ports return controlled results; do not infer backend
authorization from a component fake. HTTP cases separately assert principal-to-owner resolution, forgery rejection,
option conflicts and finite status mapping. Probe cases distinguish liveness from metadata/policy readiness and omit
private exception data. Disposed/changed selections must not accept stale answers.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Web.Analysis
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Presenter map](../../../Web/Components/Shared/Analysis/README.md)
- [Mutation mappings](../../../Web/Routes/Endpoints/README.md)
- [Probe semantics](../../../Web/Configs/Hosting/README.md)
- [Operation outcomes](../../../Application/Abstractions/Ai/Execution/README.md)
