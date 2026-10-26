# Web

ASP.NET Core/interactive-server Blazor presentation, HTTP endpoints, authorized SignalR streams and the application
composition root. The project references Application, Domain and Infrastructure.

## Contents

| File                                                         | Types or asset | Responsibility                                                                                |
|--------------------------------------------------------------|----------------|-----------------------------------------------------------------------------------------------|
| [appsettings.Development.json](appsettings.Development.json) | Configuration  | Development overrides including loopback service endpoints, without machine-specific secrets. |
| [appsettings.json](appsettings.json)                         | Configuration  | Tracked base host defaults; credentials belong in protected configuration.                    |
| [Program.cs](Program.cs)                                     | Startup        | Application entry point and ordered startup calls.                                            |
| [Web.csproj](Web.csproj)                                     | Configuration  | ASP.NET Core/Blazor project, solution references, packages and local UserSecretsId.           |

| Module                                   | Responsibility                                                      |
|------------------------------------------|---------------------------------------------------------------------|
| [Components](Components/README.md)       | Document shell, routed pages, shared controls and layout.           |
| [Configs](Configs/README.md)             | Composition root, authentication, host pipeline and options.        |
| [Extensions](Extensions/README.md)       | Deterministic endpoint discovery/registration.                      |
| [Hubs](Hubs/README.md)                   | Authorized project streams and typed browser callbacks.             |
| [Observability](Observability/README.md) | SDK export safety, resource/metric policy and HTTP audit.           |
| [Properties](Properties/README.md)       | Local IDE launch profiles.                                          |
| [Routes](Routes/README.md)               | HTTP endpoint contract and concrete mappings.                       |
| [Services](Services/README.md)           | Web transport, independent operation scopes and presentation state. |
| [wwwroot](wwwroot/README.md)             | Styles, first-party browser modules, fonts and bundled libraries.   |

## Usage and integration

Start with Components for routes/presenters, Services for transport/scoped operations, Hubs for live subscriptions and
Routes for HTTP APIs. Configs owns concrete dependency registration, authentication, profiles, startup and export
wiring; components/hubs/routes use Application contracts rather than concrete Infrastructure services.

## Contracts and lifecycle

Program builds/configures the host, initializes metadata infrastructure and runs it. Host capability/ownership checks
remain server responsibilities, not UI visibility rules. Runtime collection/approved storage are automatic; AI egress
requires separate current approval/consent and spending configuration. Deployment status and recorded completion outcome
are different.

Keep source namespaces, route shapes, callbacks and configuration keys compatible. Generated bin/obj output is excluded
from this documentation tree. Vendored/font assets retain their original contents and licensing.

## Local development

From the repository root run ./deploy/manage.ps1 start -Ide (or bash deploy/manage.sh start --ide), then select Web's
https IDE profile. The profile listens at https://localhost:7288 with supporting services in Docker. Machine-specific
endpoints/credentials belong in Web user secrets or environment configuration; Web.csproj declares the local
UserSecretsId.

## Source documentation

Use multiline XML summaries/parameter/result contracts for handwritten C# and Razor code members; component descriptions
use non-rendered Razor comments. First-party browser functions/callbacks use JSDoc describing parameters, completion and
listener/observer disposal. Preserve generated/vendor files and licenses.

## Related documentation

- [Use-case contracts](../Application/README.md)
- [Implementations](../Infrastructure/README.md)
- [Solution map](../navigation.md)
- [Web tests](../Test/Web/README.md)
- [Local IDE workflow](../deploy/README.md#preferred-workflow-web-in-the-ide)
