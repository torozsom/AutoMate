# Test/Infra/Ai

Related files are grouped by responsibility. Test namespaces use the Test.Infra prefix; scenario behavior is preserved.

- `Admission/` — `Admission/AnalysisAdmissionTests.cs`, `Admission/AnalysisBudgetPostgresTests.cs`,
  `Admission/AnalysisBudgetTests.cs`, `Admission/AnalysisConsentTests.cs`, `Admission/AnalysisEgressPolicyTests.cs`,
  `Admission/AnalysisReadinessTests.cs`.
- `ContextAndResults/` — `ContextAndResults/AnalysisContextTests.cs`, `ContextAndResults/AnalysisPersistenceTests.cs`,
  `ContextAndResults/AnalysisResultTests.cs`, `ContextAndResults/AssessmentSelectionTests.cs`.
- `Processing/` — `Processing/AnalysisQueueLeaseTests.cs`,
  `Processing/AnalysisWorkerConcurrencyTests.cs`, `Processing/FailedDeploymentTriggerPostgresTests.cs`.
- `Providers/` — `Providers/AnalysisProviderSelectionTests.cs`, `Providers/AzureOpenAiAnalysisProviderTests.cs`,
  `Providers/OpenAiAnalysisProviderTests.cs`.

Application retry-policy tests are in [App/Ai/Policy](../../App/Ai/Policy/AnalysisRetryPolicyTests.cs).
