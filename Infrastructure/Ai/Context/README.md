# Assessment evidence acquisition

Bounded archive/legacy/backend evidence with frozen deployment provenance and ephemeral context.

## Contents

| File                                                                       | Types or asset                     | Responsibility                                                                                        |
|----------------------------------------------------------------------------|------------------------------------|-------------------------------------------------------------------------------------------------------|
| [AssessmentEvidenceReader.cs](AssessmentEvidenceReader.cs)                 | `AssessmentEvidenceReader`         | Merges bounded selected archive/legacy/backend evidence without retaining unfiltered payloads.        |
| [DeploymentAnalysisContextBuilder.cs](DeploymentAnalysisContextBuilder.cs) | `DeploymentAnalysisContextBuilder` | Collects selected deployment evidence and freezes status/configuration provenance at collection time. |

## Usage and integration

DeploymentAnalysisContextBuilder loads deployment/configuration metadata and AssessmentSelection, resolves effective
focus/window and calls AssessmentEvidenceReader. Application AnalysisContextSelector applies redaction/ranking/budgets
and produces the actual evidence catalog.

## Contracts and lifecycle

Prefer IDeploymentAssessmentArchive so filtering precedes evidence limits. Compatibility paths retain explicit
unavailable/fallback behavior for older adapters. Never save unfiltered diagnostics, prompts or context snapshots to
queue/metadata rows.

Freeze status/outcome/window at collection time and use stored non-secret launch facts rather than reconstruct history
from mutable settings. Missing/unsupported/omitted data remains explicit. Rebuild evidence on retries and recheck egress
separately. A reference identifies supplied evidence, not proof the resulting narrative is correct.

## Related documentation

- [Parent module](../README.md)
- [Selection policy](../../../Application/Ai/Context/README.md)
- [Context projections](../../../Application/Abstractions/Ai/Context/README.md)
- [Archive selection](../../Diagnostics/Archive/README.md)
- [Context tests](../../../Test/Infra/Ai/ContextAndResults/AnalysisContextTests.cs)
- [Selection tests](../../../Test/Infra/Ai/ContextAndResults/AssessmentSelectionTests.cs)
