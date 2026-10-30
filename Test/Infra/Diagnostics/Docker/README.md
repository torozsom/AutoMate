# Docker diagnostic tests

Synthetic framing/ownership/replay/live metrics with separately gated real daemon/Compose smoke cases.

## Contents

| File                                                         | Test or fixture types                                                                                                 | Responsibility                                                                                     |
|--------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------|
| [DockerCollectorTests.cs](DockerCollectorTests.cs)           | `DockerCollectorTests`, `CollectorLogger`, `SdkProxy`, `UnusedLive`, `RecordingPublisher`, `DockerSmokeFactAttribute` | Synthetic collector framing/ownership tests plus explicitly gated real daemon/Compose smoke cases. |
| [DockerMetricDeliveryTests.cs](DockerMetricDeliveryTests.cs) | `DockerMetricDeliveryTests`, `TestClock`, `RecordingPublisher`, `RecordingLive`                                       | Verifies Docker live refresh is independent of durable metric sampling and delivery failures.      |
| [DockerOutputTests.cs](DockerOutputTests.cs)                 | `DockerOutputTests`                                                                                                   | Verifies stream parsing, strict ownership and confirmed overlap without contacting Docker.         |

## Coverage and navigation

DockerOutputTests exercises UTF-8/partial streams, oversize omission, Compose text/progress, strict ownership and
confirmed overlap. DockerMetricDeliveryTests separates frequent live updates from sampled durable history and transport
recovery. DockerCollectorTests uses SDK-shaped fakes for startup races/cancellation and real smoke cases for owned
lifecycle/subscription/Compose behavior.

## Fixtures and boundaries

Default fake cases need no Docker. Real_* smoke tests require AUTOMATE_DOCKER_SMOKE=1, a local daemon and an already
cached suitable shell/printf image; Compose cases also require Docker Compose. Set AUTOMATE_DOCKER_SMOKE_IMAGE to choose
one image for the cases rather than rely on their differing defaults.

Smoke tests create/clean their own identified containers/projects; keep cleanup fixture-owned. Idle-source cancellation
must not require stopping the target. Expected initial missing containers differ from permission/connectivity failures.
The ten-project Compose case is opt-in, not part of ordinary synthetic capacity claims.

## Running

From the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~Test.Infra.Diagnostics.Docker
```

Explicit integration gates still apply; skipped cases do not verify the corresponding external service.

### Real smoke selection

After explicitly enabling the gate and selecting an already cached image, run from the repository root:

```powershell
dotnet test Test/Test.csproj --filter FullyQualifiedName~DockerCollectorTests.Real_
```

This command contacts the local daemon only when the smoke attribute's opt-in is enabled.

## Related documentation

- [Parent module](../README.md)
- [Sources/parsing](../../../../Infrastructure/Docker/Diagnostics/README.md)
- [Supervisor](../../../../Infrastructure/ApplicationServices/Orchestration/Local/README.md)
- [Host lifecycle tests](../../Orchestration/LocalDiagnosticSupervisionTests.cs)

Metric delivery tests use FakeTimeProvider for trailing updates and verify latest-value bursts, original timestamps,
independent typed/legacy failures, unchanged history cadence and no pending callback after disposal.
