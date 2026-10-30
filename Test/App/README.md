# Application tests

Portable Application-owned policies and cloud admission use cases, independent of Web renderer fixtures.

`ProjectionContractTests.cs` protects JSON cost/provenance, history continuation, metric paging,
inventory timestamps and resource units, including null legacy metadata.

## Contents

| Module                                   | Coverage                                                |
|------------------------------------------|---------------------------------------------------------|
| [Ai](Ai/README.md)                       | Application-owned AI policy coverage.                   |
| [Orchestration](Orchestration/README.md) | Cloud admission routing and form validation precedence. |

## Coverage and navigation

Ai/Policy covers bounded retry timing. Orchestration covers cloud form validation/first-error order and
SelfHosted-versus-SaaS request routing through synthetic ports. Production contracts/policies are the extension point;
these tests should assert observable behavior rather than component private state.

## Fixtures and boundaries

Fixtures contain synthetic deployment configuration and recording ports. They require no provider credentials, database,
Docker or browser. Preserve input/default/route compatibility and distinguish policy checks from provider-specific
validation.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.App
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [AI policy](../../Application/Ai/README.md)
- [Workflow contracts](../../Application/Orchestration/README.md)
- [Persistence/provider tests](../Infra/README.md)

LegacyContractTests protects persisted enum ordinals, repository identity and retained legacy snapshot JSON linkage,
trace and expiry metadata. Retaining a legacy model does not authorize new diagnostic snapshot persistence.
