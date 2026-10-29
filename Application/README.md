# Application

Application-owned use-case contracts, outbound ports, deployment coordination and provider-independent AI policies. The
project targets .NET 10/C# 14, references Domain only among solution projects, and uses Microsoft.Extensions.Hosting for
hosted workers, logging, options and dependency injection.

## Contents

| File                                     | Responsibility                                                                       |
|------------------------------------------|--------------------------------------------------------------------------------------|
| [Application.csproj](Application.csproj) | .NET 10/C# 14; Domain project reference and Microsoft.Extensions.Hosting dependency. |

| Module                                   | Responsibility                                                                       |
|------------------------------------------|--------------------------------------------------------------------------------------|
| [Abstractions](Abstractions/README.md)   | Outbound ports and shared adapter contracts.                                         |
| [Ai](Ai/README.md)                       | Provider-independent assessment selection, execution policies and result validation. |
| [Auth](Auth/README.md)                   | Account registration, login, verification and provider-linking contract.             |
| [Data](Data/README.md)                   | Owner-scoped project, deployment, workspace and user contracts.                      |
| [Diagnostics](Diagnostics/README.md)     | Application activity sources, meters, audit events and diagnostic options.           |
| [Orchestration](Orchestration/README.md) | Deployment admission, in-memory scheduling and status notification.                  |

## Usage and integration

Start with the module indexes above. Data/Auth define account and project operations; Abstractions owns adapter
boundaries; Orchestration coordinates deployment work; Ai implements assessment policies; Diagnostics provides safe
operational instrumentation.

Infrastructure implements database/provider operations. Web hosts the composition root, UI, routes and live transport.
Register callable ports through the host rather than referencing Infrastructure implementation classes from Application.
Existing consumers are linked from each leaf guide.

## Contracts and lifecycle

Application contains no EF Core or provider SDK dependency. Its concrete policies, scheduler and notifier are distinct
from interfaces implemented elsewhere. In-memory deployment jobs are lost on process exit; durable SaaS runs and AI
execution use separate persistence-backed ports.

Keep public namespaces, optional/default interface behavior, configuration keys and serialized contracts compatible.
Read leaf guides before extending a boundary; interface declarations alone do not establish authorization or redaction
guarantees.

### Source documentation

Preserve concise multiline XML summaries on handwritten types, constructors and methods, including private helpers and
nested types. Document parameters/type parameters, results and actual exceptional conditions;
fields/properties/events/enum members may use single-line summaries. Keep ownership, null results, bounds and
compatibility defaults explicit. Do not replace method documentation with standalone inheritdoc tags or invent framework
failures. Local-function summaries use ordinary source comments with XML-formatted text because C# does not emit XML
documentation for local declarations.

## Related documentation

- [Domain models](../Domain/README.md)
- [Implementations](../Infrastructure/README.md)
- [Composition and registrations](../Web/Configs/Hosting/ServiceConfiguration.cs)
- [Solution navigation](../navigation.md)
- [Tests](../Test/README.md)
