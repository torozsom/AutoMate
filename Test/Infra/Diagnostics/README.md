# Diagnostic pipeline tests

Redaction, delivery, temporary spool/permanent archive, analytics and Docker source behavior.

## Contents

| Module                                         | Coverage                                                              |
|------------------------------------------------|-----------------------------------------------------------------------|
| [Archive](Archive/README.md)                   | Durability, replay, corruption, backfill, deletion and analytics.     |
| [Delivery](Delivery/README.md)                 | Sink isolation, viewers, private protocol and opt-in real storage.    |
| [Docker](Docker/README.md)                     | Synthetic parsing/supervision plus opt-in daemon/Compose smoke tests. |
| [PolicyAndMetrics](PolicyAndMetrics/README.md) | Masking, storage/transport limits, native units and metric lookup.    |

## Coverage and navigation

Archive covers durable history/replay/corruption/backfill/deletion. Delivery covers deadlines/confirmation, viewer
leases, private protocol and opt-in real stores. Docker covers stream framing/ownership/live sampling with separately
gated smoke cases. PolicyAndMetrics covers masking/finite limits, native units and numeric lookup recovery.

## Fixtures and boundaries

Use synthetic credentials/output and assert actual safe copies/wire fields rather than just helper return values.
Temporary buffers, operational backends, permanent archives and saved analysis results have different lifetimes.
Preserve explicit opt-ins and fixture cleanup; default synthetic tests do not prove live Docker/storage availability.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra.Diagnostics
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

## Related documentation

- [Parent module](../README.md)
- [Pipeline map](../../../Infrastructure/Diagnostics/README.md)
- [Contracts](../../../Application/Abstractions/Diagnostics/README.md)
- [Host supervision](../Orchestration/README.md)
- [Final transport/export checks](../../Web/Diagnostics/README.md)
