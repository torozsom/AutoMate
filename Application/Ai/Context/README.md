# Assessment selection and evidence

Canonical assessment choices, frozen evidence ranges, bounded redacted context and exact citation membership.

## Contents

| File                                                     | Types                                                                                                                                                   | Responsibility                                                                                           |
|----------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------|
| [AnalysisContextBudget.cs](AnalysisContextBudget.cs)     | `AnalysisContextBudget`                                                                                                                                 | Consistent context limits, including conservative provider-input token accounting.                       |
| [AnalysisContextSelector.cs](AnalysisContextSelector.cs) | `AnalysisContextSelector`                                                                                                                               | Selects bounded diagnostic evidence using assessment focus, source representation, severity and recency. |
| [AnalysisEvidence.cs](AnalysisEvidence.cs)               | `AnalysisEvidence`                                                                                                                                      | Rejects fabricated or excluded evidence before provider return and result persistence.                   |
| [AssessmentSelection.cs](AssessmentSelection.cs)         | `AssessmentKind`, `AssessmentRange`, `AssessmentSources`, `AssessmentSelection`, `AssessmentWindow`, `AssessmentProvenance`, `AssessmentClassification` | Canonical request choices, status-aware focus/ranges, frozen provenance and source/channel matching.     |

## Usage and integration

AssessmentSelection normalizes choices and serializes canonical request options. It resolves automatic focus/range from
deployment status; AssessmentClassification maps normalized events to requested sources. AnalysisContextSelector
receives terminal history, numeric metrics and a budget, returning ephemeral DeploymentAnalysisContext. AnalysisEvidence
checks returned references against the supplied catalog.

## Contracts and lifecycle

Custom selections require ordered timestamps, at most 365 days and bounded container names; Normalize converts
timestamps to UTC and sorts/deduplicates names ordinally. Window clips to deployment creation, rejects future end times
and truncates lifetime evidence to 365 days. AssessmentProvenance records requested/effective choices, status/outcome
and collection window without diagnostic payloads.

Selector examines at most 1,000 candidate terminal events, deduplicates by event/order identity, redacts before grouping
and retains chronological selected output. Failure diagnosis prioritizes severity/failure terms; other focuses give
channels representation before recency. Logs use at most three quarters of budgets when metric/trace signals exist.
Numeric summaries accept only the supported finite nonnegative CPU/memory series, keep container groups separate and
bound candidate/group counts. Trace summaries count actual correlated selected events, not stored spans.

AnalysisContextBudget defaults via AiAnalysisOptions to 24,000 characters, 48,000 encoded bytes and 12,000 conservative
token units. Each encoded provider-input byte consumes one token unit, including JSON escaping; this is not an exact
tokenizer or a full billable-request count. Bounds clamp to 1–131,072. Empty configuration-only context never qualifies
as evidence.

AnalysisEvidence uses exact ordinal membership when a catalog is supplied; null retains legacy shape-only behavior.
Membership proves a reference was supplied, not that the recommendation is correct. Coverage records
omissions/unavailability; evidence references can cease to resolve after source retention.

## Related documentation

- [Parent module](../README.md)
- [Context ports and projections](../../Abstractions/Ai/Context/README.md)
- [Collection consumer](../../../Infrastructure/Ai/Context/DeploymentAnalysisContextBuilder.cs)
- [Archive evidence retrieval](../../../Infrastructure/Ai/Context/AssessmentEvidenceReader.cs)
- [Context tests](../../../Test/Infra/Ai/ContextAndResults/AnalysisContextTests.cs)
- [Selection tests](../../../Test/Infra/Ai/ContextAndResults/AssessmentSelectionTests.cs)
