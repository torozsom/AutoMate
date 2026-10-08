# Routes

Minimal API endpoint contracts and endpoint implementations.
Login attempts/outcomes and authenticated logout are audited by their originating endpoint modules. Successful events
retain the internal user GUID without email/password or cookie data. Routed requests also cross RequestAuditMiddleware.
The SaaS-only GitHub App webhook endpoint accepts bounded HTTPS POST bodies and delegates signature verification and
minimal metadata persistence to Infrastructure.

## Source inventory

- `IEndpoint.cs`

## Boundary

Keep presentation behavior here. Components, hubs, routes, and services use Application contracts; concrete
Infrastructure types are registered only in Web/Configs.

## Related documentation

- [Solution navigation map](../../navigation.md)

`Endpoints/DeploymentHistoryEndpoint.cs` provides owner-authorized cursor log pages and bounded metric-range APIs.

## Deployment telemetry update

Both SelfHosted and SaaS use the private Telemetry disk gateway for new deployment logs and metrics. PostgreSQL payload
writes are rejected at application startup; legacy reads and draining of existing outbox rows remain available. The
gateway confirms durable checksummed writes before cloud checkpoints advance. Tenant-scoped v2 history, deployment
revision recovery and weighted daily project analytics are documented in [the rollout guide](/docs/saas-telemetry.md).
Detailed data expires after 30 days; daily statistics after 365 days. See the root navigation.md for new module entry
points.

`Endpoints/DeploymentAnalysisEndpoint.cs` maps `DELETE /api/deployments/{deploymentId}/analyses/{analysisId}`.
It resolves the owner from the authenticated principal using `IUserService`, requires a valid antiforgery cookie/token
(the default `RequestVerificationToken` header or `__RequestVerificationToken` form field), and calls the Application
analysis deletion port. Responses: 204 deleted, 404 missing/foreign, 409 unexpired queued/running, 400 invalid
antiforgery.
An authenticated owner may delete results with AI disabled. The analysis panel exposes cancellation, but no delete
control; deletion is available through this API. Unresolved owner identity returns 403.

Local login emits fixed authentication completion/denial events without email, password, user or returned error text.
Endpoint authorization challenges/forbids are audited by the Web composition-root handler while retaining framework
responses. See `Application/Diagnostics/README.md` for the fixed event contract and supported export boundaries.

POST /api/deployments/{deploymentId}/analyses/{analysisId}/cancel requires authentication and a valid antiforgery
cookie/token, resolves owner identity through IUserService and calls IDeploymentAnalysisService.CancelAsync. Responses:
204 canceled/already canceled, 404 missing/foreign/expired, 409 already completed/failed/skipped, 400 invalid
antiforgery,
and 403 unresolved owner. Cancellation is available with AI disabled. It does not delete results or alter deployments;
the project analysis panel also exposes cancellation through the Application port.
