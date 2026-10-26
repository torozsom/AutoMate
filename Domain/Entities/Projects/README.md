# Projects and saved configuration

The hierarchy from a user-owned application source to discovered C# projects, saved settings and deployments.

## Contents

| File                                 | Types and responsibility                                                                                |
|--------------------------------------|---------------------------------------------------------------------------------------------------------|
| [Application.cs](Application.cs)     | Application: user-owned source repository/folder and collection of C# projects.                         |
| [CsProject.cs](CsProject.cs)         | CsProject: one .csproj belonging to an application, with optional configuration and deployment history. |
| [Configuration.cs](Configuration.cs) | Configuration: saved runtime/cloud settings, environment JSON and separate AI egress consent.           |

## Usage and integration

Application represents imported source, not the Application-layer project. It owns CsProject entries; each can hold
Configuration and multiple
deployments. [ApplicationService](../../../Infrastructure/ApplicationServices/Data/Apps/ApplicationService.cs)
and [ProjectDetailsQuery](../../../Infrastructure/ApplicationServices/Data/Apps/ProjectDetailsQuery.cs) implement
persistence and authorized reads.

## Contracts and lifecycle

Application.UserId identifies the owner; CsProject.AppId links the C# project to that application. Fields named
ProjectId elsewhere can refer to different hierarchy levels; follow the consumer's relationship mapping.

Configuration stores per-project settings and optional environment JSON that may contain secrets and requires protected
handling. IsPublic records requested exposure, not authorization or resource availability. Nullable settings can
represent a missing saved choice; deployment preparation applies defaults.

RuntimeDiagnosticsEnabled and ManagedTelemetryConsent default to true and remain compatibility fields: runtime
collection and approved storage are automatic. Configuration.AiDiagnosticEgressConsented is a separate owner-granted AI
egress decision enforced by admission/egress services.

## Related documentation

- [Entities index](../README.md)
- [Relationship mappings](../../../Infrastructure/Data/AutoMateModelConfiguration.cs)
- [Discovery DTOs](../../DTO/Projects/README.md)
- [Account owners](../Users/README.md)
- [Configuration loading tests](../../../Test/Web/Projects/ConfigurationVariableLoadingTests.cs)
- [AI consent tests](../../../Test/Infra/Ai/Admission/AnalysisConsentTests.cs)
