# Local durable-ingestion benchmark

Executed on 2026-10-04 using the Windows workspace disk and 1 kB log messages.

300 deployments; 16000 durable log receipts; 100/s for 60s + 1000/s for 10s; elapsed=70,036s; segmentFiles=225

This measures the disk-spool ingestion path across 300 deployment identities. It does not measure gateway HTTP, Azure
collection, delivery throughput or production capacity. The test asserts unique event IDs and positions and waits for
every durable receipt. Reproduce with AUTOMATE_TELEMETRY_EXTENDED_LOAD=1 and the Pilot_load_300 test filter.

## Additional checks

- Complete .NET suite: 81 passed, including six real PostgreSQL/Loki/Mimir integration tests; no skips in this
  configured run.
- Terminal JavaScript suite: four passed.
- Linux Telemetry Docker-image build passed; final solution build had zero warnings and errors.
- Container HTTP: unauthenticated 401, another tenant's pending history 403, durable receipt and secret redaction
  confirmed.
- Isolated Loki/Mimir outage: acknowledged event survived telemetry-container restart; delivery drained after backends
  restarted.
- EF snapshot matches migrations. Changed-file format verification and git diff whitespace checks passed.
  Whole-repository format verification reports pre-existing errors in unrelated files.

The temporary integration containers, volumes and credentials were removed after verification. Application databases
were not migrated or rewritten. Azure calls were verified with deterministic adapter tests, including parallel
deployment revision isolation; no real Azure deployment was launched.
