# Web composition root

Dependency registration, host pipeline, OAuth integration and validated operational options.

## Contents

| Module                                   | Responsibility                                                        |
|------------------------------------------|-----------------------------------------------------------------------|
| [Azure](Azure/README.md)                 | OAuth claim extraction and subscription lookup.                       |
| [Hosting](Hosting/README.md)             | Service composition, profiles, callbacks, pipeline and health probes. |
| [Observability](Observability/README.md) | Validated exporter options and authorization audit integration.       |

## Usage and integration

Hosting registers Application ports and concrete Infrastructure implementations, maps middleware/endpoints/probes and
delegates account callbacks. Azure supports subscription/claim extraction. Observability validates export options and
integrates authorization audit handling. Runtime SDK processors live in the separate Web Observability folder.

## Contracts and lifecycle

SelfHosted enables local/cloud workflows; SaaS is cloud-only and requires GitHub App configuration. Both use private
telemetry gateway configuration. Keep provider clients, scoped/hosted lifetimes, key-ring protection and validated
options here, not in component code. Default-off AI flags and zero spending settings do not enable provider calls.

## Liveness and AI readiness

/health is lightweight process liveness. /health/ready runs a five-second read-only AI configuration/metadata probe and
returns finite uncached status/configuration/queue JSON. Both allow anonymous probes; ordinary authorization remains
unchanged. See [Hosting](Hosting/README.md) for HTTP outcomes and limits.

## Related documentation

- [Parent module](../README.md)
- [Scoped UI boundaries](../Services/README.md)
- [Concrete adapters](../../Infrastructure/README.md)
- [Composition regressions](../../Test/Web/Hosting/HostingProfileRegistrationTests.cs)
- [AI provider setup](../../docs/azure-openai-setup.md)
