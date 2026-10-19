# Application HTTP mappings

Authorized analysis/history APIs, signed GitHub ingress, static assets and interactive Razor mapping.

## Contents

| File                                                           | Types or asset                                    | Responsibility                                                                                                                      |
|----------------------------------------------------------------|---------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------|
| [DeploymentAnalysisEndpoint.cs](DeploymentAnalysisEndpoint.cs) | `DeploymentAnalysisEndpoint`, `AssessmentRequest` | Authenticated preference/admission/cancel/delete APIs and typed AssessmentRequest.                                                  |
| [DeploymentHistoryEndpoint.cs](DeploymentHistoryEndpoint.cs)   | `DeploymentHistoryEndpoint`                       | Authenticated bounded history APIs; provider query languages are never accepted.                                                    |
| [GitHubAppWebhookEndpoint.cs](GitHubAppWebhookEndpoint.cs)     | `GitHubAppWebhookEndpoint`                        | Public HTTPS ingress for signed GitHub App workflow notifications.                                                                  |
| [RazorComponentsEndpoint.cs](RazorComponentsEndpoint.cs)       | `RazorComponentsEndpoint`                         | Endpoint for Razor Components. Maps the root path to the App component and enables interactive server render mode.                  |
| [StaticAssetsEndpoint.cs](StaticAssetsEndpoint.cs)             | `StaticAssetsEndpoint`                            | Endpoint for serving static assets. Registered before other endpoints to ensure static files are served correctly and performantly. |

| Module                 | Responsibility                                           |
|------------------------|----------------------------------------------------------|
| [Auth](Auth/README.md) | Local cookie login/logout and GitHub/Azure OAuth routes. |

## Usage and integration

DeploymentAnalysisEndpoint delegates owner-scoped preferences/admission/cancellation/deletion to
IDeploymentAnalysisService. DeploymentHistoryEndpoint exposes bounded log/metric/project analytics reads.
GitHubAppWebhookEndpoint accepts bounded HTTPS signed payloads only for the SaaS route. StaticAssetsEndpoint and
RazorComponentsEndpoint supply framework endpoints; Auth contains account routes.

## Contracts and lifecycle

Owners resolve from the authenticated principal, never request bodies. AI PUT/POST/DELETE validates antiforgery
(RequestVerificationToken header or __RequestVerificationToken form field). Invalid tokens/selection return 400 and
unresolved owner 403. Deletion/cancellation returns 204, scoped missing 404 or active/finished conflict 409 as
appropriate; these actions remain available with AI disabled.

Typed admission requires nonempty RequestId/Selection; normalized-option conflicts return 409, otherwise HTTP 200
contains the Application request result, whose Accepted flag must be inspected. Preferences use GET/PUT under the
deployment analysis path. The panel exposes cancel, not delete.

History supports v2 scoped cursors/channel/search and legacy numeric cursors; ranges/paging are bounded and provider
query languages are not accepted. Invalid reads fail explicitly instead of silently widening scope. Webhook signature
verification remains an Application port even though HTTP ingress allows anonymous delivery.

## Route map

| Path family                                                           | Purpose                                                                                                  |
|-----------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------|
| /api/deployments/{deploymentId}/analyses                              | POST selected assessment request; GET/PUT /preferences; DELETE /{analysisId}; POST /{analysisId}/cancel. |
| /api/v2/projects/{projectId}/deployments/{deploymentId}/logs          | Owner-authorized cursor/channel/search history.                                                          |
| /api/v2/projects/{projectId}/analytics                                | Bounded project aggregates.                                                                              |
| /api/projects/{projectId}/deployments/{deploymentId}/logs or /metrics | Legacy history/metric reads.                                                                             |
| /api/github/app/webhook                                               | SaaS signed workflow notifications.                                                                      |

GUID constraints remain in the actual mappings; health and /loghub are mapped by Hosting rather than these classes.

## Related documentation

- [Parent module](../README.md)
- [Analysis outcomes](../../../Application/Abstractions/Ai/Execution/README.md)
- [Cursor contracts](../../../Application/Abstractions/Diagnostics/History/README.md)
- [Signature handling](../../../Infrastructure/GitHub/Webhooks/README.md)
- [HTTP mutation tests](../../../Test/Web/Analysis/DeploymentAnalysisEndpointTests.cs)
- [History tests](../../../Test/Web/Projects/DeploymentHistoryTests.cs)
