# Infrastructure tests

Adapter, relational persistence, workflow, durability and provider-protocol regressions.

## Contents

| Module                                   | Coverage                                                                         |
|------------------------------------------|----------------------------------------------------------------------------------|
| [Ai](Ai/README.md)                       | Admission/budgets, context/results, processing and provider adapters.            |
| [Auth](Auth/README.md)                   | Registration compensation and verification/login boundaries.                     |
| [Azure](Azure/README.md)                 | Synthetic Azure provisioning, metrics/logs and runtime source tests.             |
| [Data](Data/README.md)                   | EF model consistency, bounded reads and opt-in PostgreSQL workspace projections. |
| [Diagnostics](Diagnostics/README.md)     | Redaction, archive/spool, delivery, metrics and Docker sources.                  |
| [GitHub](GitHub/README.md)               | Signed ingress, secure downloads, log/progress checkpoints and commit recovery.  |
| [Orchestration](Orchestration/README.md) | Queue scheduling, local supervision, cloud admission and startup recovery.       |
| [Scanning](Scanning/README.md)           | Filesystem failure versus empty environment scans.                               |
| [Templating](Templating/README.md)       | Local ownership and Azure registry/workflow/diagnostic artifacts.                |
| [TestSupport](TestSupport/README.md)     | Reusable synthetic HTTP factory and message handler.                             |

## Coverage and navigation

Follow the module table for AI, Azure, diagnostics/Docker, GitHub, authentication, data, orchestration, scanning and
templates. TestSupport holds reusable synthetic HTTP helpers. Source tables in leaf guides map each test class and
nested fixture to its file.

## Fixtures and boundaries

Default cases use controlled responses and owned temporary resources; some test real SDK serialization, SQLite SQL and
loopback HTTP rather than mocked method calls alone. PostgreSQL/Docker/telemetry-stack coverage is explicitly gated.
SQLite does not prove PostgreSQL advisory-lock/trigger behavior, and synthetic provider success does not certify live
credentials or deployment readiness.

Keep sensitive strings synthetic and assert they cannot escape safe fields. Cleanup must target fixture-owned resources;
do not substitute an application database for isolated verification.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Shared source conventions

Document scenario intent, fixture recording/fixed responses, ignored inputs and deliberate rejection paths. Preserve XML
summaries/parameters/completion contracts and narrow local-function CS1587 suppression where present. Production
internals remain internal; friend-assembly access avoids adding public APIs solely for tests.

## Related documentation

- [Parent module](../README.md)
- [Implementation map](../../Infrastructure/README.md)
- [Portable policy tests](../App/README.md)
- [Host/UI coverage](../Web/README.md)
- [Owned PostgreSQL verification](../../deploy/verification/README.md)
