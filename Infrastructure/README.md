# Infrastructure

EF Core persistence, external-system adapters and concrete implementations of Application ports. This .NET 10/C# 14
project references Application and Domain; provider SDKs and runtime assets belong here.

## Contents

| File                                           | Types or asset        | Responsibility                                                      |
|------------------------------------------------|-----------------------|---------------------------------------------------------------------|
| [Infrastructure.csproj](Infrastructure.csproj) | Project configuration | Project references, provider packages and runtime asset-copy rules. |

| Module                                               | Responsibility                                                           |
|------------------------------------------------------|--------------------------------------------------------------------------|
| [Ai](Ai/README.md)                                   | AI admission, evidence, durable processing and provider adapters.        |
| [ApplicationServices](ApplicationServices/README.md) | EF-backed account/project services and deployment workflows.             |
| [Azure](Azure/README.md)                             | ARM provisioning, identity and owned runtime/database collection.        |
| [Data](Data/README.md)                               | EF context, mappings, protected conversions and failure-capture SQL.     |
| [Diagnostics](Diagnostics/README.md)                 | Redacted ingestion, delivery, storage, archive and analytics.            |
| [Docker](Docker/README.md)                           | Docker SDK/CLI operations, builds and local diagnostic sources.          |
| [Email](Email/README.md)                             | SMTP email transport and configuration.                                  |
| [GitHub](GitHub/README.md)                           | Repository/workflow API, installation credentials, secrets and webhooks. |
| [Migrations](Migrations/README.md)                   | Ordered EF schema history grouped by responsibility.                     |
| [Observability](Observability/README.md)             | Shared platform logging policy and console exception diagnostics.        |
| [Properties](Properties/README.md)                   | Assembly-level internal test visibility.                                 |
| [Scanner](Scanner/README.md)                         | Filesystem discovery, .NET metadata and environment extraction.          |
| [Templating](Templating/README.md)                   | Manifest-driven deployment artifact rendering.                           |

## Usage and integration

Start with the module table to locate persistence, workflows, provider adapters and telemetry. Application owns the
contracts; Web and the private Telemetry host register implementations. Infrastructure does not reference Web. Most
helper classes are internal; use Application ports from presentation code and keep concrete registrations in the host
composition root.

## Contracts and lifecycle

Separate account/project metadata in PostgreSQL from diagnostic payloads. Current new deployment logs/metrics pass
through the private telemetry gateway; PostgreSQL retains legacy history/drain compatibility. Permanent archives and
daily summaries retain until project deletion, while operational Loki/Mimir data uses its independent retention.

AI requests remain default-off and require current policy, account approval and project consent; no prompt/context
payload is queued. Source folders are navigation groups and may retain broader existing namespaces. Preserve public
contracts, configuration keys, migration IDs, provider behavior and secret handling.

## Dependency and source documentation

Infrastructure.csproj explicitly pins System.Security.Cryptography.Pkcs to override the MailKit/MimeKit transitive
minimum. Preserve the intentional override when updating dependencies and audit the resulting package graph; this
documentation pass does not change or re-audit packages.

Use concise multiline XML summaries for handwritten types, constructors, methods and local functions, including
internal/private/nested declarations. Document parameters/type parameters, results and actual exception conditions;
fields/properties may use single-line summaries. Adapter comments must describe concrete ownership, cancellation,
durability and failure behavior. Preserve EF generated markers, migration identities and SQL/model operations;
regeneration may replace generated comments. Local-function XML uses ordinary source comments so the compiler needs no
documentation-warning suppression.

## Related documentation

- [Application contracts](../Application/README.md)
- [Domain models](../Domain/README.md)
- [Web composition](../Web/Configs/Hosting/ServiceConfiguration.cs)
- [Private host composition](../Telemetry/Program.cs)
- [Solution map](../navigation.md)

Shared safe OTel SDK integration resides in Observability/OpenTelemetry and is consumed by both hosts. Infrastructure
owns the SDK dependency; Application retains SDK-independent operation sources/meters. See the submodule README.
