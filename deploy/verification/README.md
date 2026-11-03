# Isolated verification

Run AI PostgreSQL acceptance from the repository root with PowerShell and an already cached `postgres:17-alpine` image:

```powershell
./deploy/verification/Test-AiPostgres.ps1
```

The script creates its own labeled container, random loopback port, random temporary credential and tmpfs database.
It overrides `AUTOMATE_AI_TEST_DB` only for its child test process and restores the previous process environment.
Tests create uniquely named schemas, apply the real ordered EF migrations and remove only those schemas. Finally the
runner verifies ownership before removing its own container/volumes. It never reads Web secrets, uses an existing
database/container or invokes a provider. A cached image is required; the runner does not pull one or provision Azure.

Coverage includes actual failure-trigger transition/rollback capture, concurrent owner admissions across projects,
project deletion without quota refund, shared PostgreSQL advisory-lock spending and tenant/global capacity, same-lease
reuse, recovered-lease charges, cancellation while actually waiting for the lock, and retained admission backfill.

For another isolated PostgreSQL test database, explicitly set `AUTOMATE_AI_TEST_DB` and run the same test filter. These
tests execute migrations and create/drop generated schemas, so that variable must never point at an application or
production database. Default .NET suites skip these cases when the variable is absent. Test schema deletion is not an
operator production migration or retention drill.

This verifies PostgreSQL semantics on one local instance. Production encryption/identities, physical backend cleanup,
backup/restore expiry, sustained end-to-end capacity, HA and live provider quality still require environment acceptance.

## Observability regression soak

Run ./deploy/verification/Test-ObservabilitySoak.ps1 for the default 20-minute local regression soak. It snapshots
Release
test binaries and repeats real SDK/loopback, temporary spool, fake-time bursts, collector interruption and synthetic
analysis-worker scenarios. Use -NoBuild only after building Release. Each iteration log and status.json are saved under
.tmp/observability-soak; use a different -OutputDirectory for concurrent or separately retained runs. It never restarts
or interrupts application deployments and does not call a real cloud/LLM provider. This verifies repeated regression
behavior, not live-provider throughput, persistent production trace retention or CPU equivalence to a previous build.
