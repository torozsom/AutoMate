# Outbound ports

Application-owned interfaces and shared data used to cross provider, host and transport boundaries.

## Contents

| Module                               | Responsibility                                                          |
|--------------------------------------|-------------------------------------------------------------------------|
| [Ai](Ai/README.md)                   | AI context, durable execution, policy gates and provider results.       |
| [Azure](Azure/README.md)             | Azure federation, runtime streaming and scoped monitoring tokens.       |
| [Diagnostics](Diagnostics/README.md) | Normalized deployment events, delivery, history, metrics and redaction. |
| [Docker](Docker/README.md)           | Docker operations and local diagnostic-source supervision.              |
| [Email](Email/README.md)             | Outbound email delivery.                                                |
| [GitHub](GitHub/README.md)           | Repository/workflow operations, installation credentials and webhooks.  |
| [Hosting](Hosting/README.md)         | Host-profile deployment capabilities.                                   |
| [Logging](Logging/README.md)         | Safe live terminal and numeric metric delivery.                         |
| [Scanning](Scanning/README.md)       | Local discovery, .NET metadata and environment extraction.              |
| [Templating](Templating/README.md)   | Deployment artifact rendering and writing.                              |

## Usage and integration

Select a child module for the external capability you need. Infrastructure implements most ports; Web implements live UI
streaming. Callable account/project operations are indexed separately under Data and Auth. Add orchestration policy in
Application and place provider SDK, HTTP, filesystem and EF implementations in Infrastructure.

## Contracts and lifecycle

Ports carry Domain models or Application-owned contracts rather than provider SDK types. Callers must propagate
cancellation and respect documented nullable/availability results. Interface default methods preserve older adapters;
some return empty/unavailable results, ignore additive options or throw for unsupported features. Read the relevant leaf
guide before assuming support.

## Related documentation

- [Parent module](../README.md)
- [Data contracts](../Data/README.md)
- [Authentication](../Auth/README.md)
- [Adapter implementations](../../Infrastructure/README.md)
- [Port registrations](../../Web/Configs/Hosting/ServiceConfiguration.cs)
