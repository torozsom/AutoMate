# Web/Components/Shared

Related files are grouped by responsibility. Existing .NET namespaces and public identities are preserved.

- `Analysis/` — `Analysis/AssessmentContextControls.razor`, `Analysis/AssessmentResultSection.razor`,
  `Analysis/DeploymentAnalysisPanel.razor`, `Analysis/DeploymentAnalysisPanel.razor.css`,
  `Analysis/DeploymentAnalysisSection.razor`.
- `Configuration/` — `Configuration/ConfigurationForm.razor`, `Configuration/ConfigurationForm.razor.cs`,
  `Configuration/DeploymentConfigurationDetails.razor`, `Configuration/RecordedValueRow.razor`.
- `Console/` — `Console/ConsoleDialog.razor`, `Console/Terminal.razor`, `Console/ThemeToggle.razor`,
  `Console/DeploymentTerminalPresentation.cs`.
- `Metrics/` — `Metrics/MetricRangePicker.razor`, `Metrics/MetricStatisticsView.razor`,
  `Metrics/ProjectTelemetrySummary.razor`, `Metrics/StatisticsPager.razor`, `Metrics/TelemetryChart.razor`,
  `Metrics/TelemetryPreferences.razor`, `Metrics/TelemetryPresentation.cs`.
- `Projects/` — `Projects/DeploymentBadge.razor`, `Projects/ProjectSectionNavigation.razor`,
  `Projects/WorkflowBadge.razor`, `Projects/GitHubRepositoryReference.cs`, `Projects/GitHubRepositoryUrlParser.cs`.
- `Workspace/` — `Workspace/KpiCard.razor`, `Workspace/ProjectInventoryTable.razor`,
  `Workspace/WorkspaceOverviewPanel.razor`, `Workspace/WorkspaceOverviewView.razor`.

## Application policy and scope boundaries

ConfigurationForm delegates its existing cloud validation rules and first-error precedence to
Application.Orchestration.CloudRuntimeConfigurationValidation. The form still owns binding, local port validation,
default-name editing and deployment confirmation. Provider/template validation remains separate and unchanged.
TelemetryPreferences resolves owner and history dependencies within a fresh operation scope instead of sharing circuit
EF services. Its existing loading, selection and failure messages are unchanged.
