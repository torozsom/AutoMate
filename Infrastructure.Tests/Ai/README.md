# Infrastructure.Tests/Ai

Related files are grouped by responsibility. Existing .NET namespaces and public identities are preserved.

- `Admission/` — `Admission/AnalysisAdmissionTests.cs`, `Admission/AnalysisBudgetPostgresTests.cs`, `Admission/AnalysisBudgetTests.cs`, `Admission/AnalysisConsentTests.cs`, `Admission/AnalysisEgressPolicyTests.cs`, `AnalysisReadinessTests.cs`.
- `ContextAndResults/` — `ContextAndResults/AnalysisContextTests.cs`, `ContextAndResults/AnalysisPersistenceTests.cs`, `ContextAndResults/AnalysisResultTests.cs`, `ContextAndResults/AssessmentSelectionTests.cs`.
- `Processing/` — `Processing/AnalysisQueueLeaseTests.cs`, `Processing/AnalysisRetryPolicyTests.cs`, `Processing/AnalysisWorkerConcurrencyTests.cs`, `Processing/FailedDeploymentTriggerPostgresTests.cs`.
- `Providers/` — `Providers/AnalysisProviderSelectionTests.cs`, `Providers/AzureOpenAiAnalysisProviderTests.cs`, `Providers/OpenAiAnalysisProviderTests.cs`.
