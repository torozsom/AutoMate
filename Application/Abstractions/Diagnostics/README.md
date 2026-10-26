# Deployment diagnostic contracts

Provider-neutral observations and ports for safe ingestion, delivery, history, metric exploration and redaction.

## Contents

| Module                           | Responsibility                                                                 |
|----------------------------------|--------------------------------------------------------------------------------|
| [Delivery](Delivery/README.md)   | Durable gateway acceptance, pending history, capabilities and viewer interest. |
| [Events](Events/README.md)       | Versioned normalized observations and terminal projections.                    |
| [History](History/README.md)     | Deployment archives, bounded replay, scoped cursors and assessment reads.      |
| [Metrics](Metrics/README.md)     | Typed samples, log/metric ports, chart windows and analytics projections.      |
| [Redaction](Redaction/README.md) | Safe event/text copies and shared terminal redaction.                          |

## Usage and integration

Collectors normalize Docker/GitHub/Azure output into Events. Redaction produces safe copies before
buffering/export/transport. Delivery crosses the durable telemetry gateway; History provides archive/replay; Metrics
defines numeric observations and owner-facing read services. Live UI output uses the separate Logging port.

## Contracts and lifecycle

These contracts are independent of SignalR, EF and the OpenTelemetry SDK. Current new deployment logs/metrics use the
private telemetry gateway; PostgreSQL contracts retain legacy read/drain compatibility. Runtime collection and approved
storage are automatic; viewer presence is not storage authorization and AI egress still requires separate consent.

History consumers must preserve availability/omission/cursor flags. Channel filtering must happen before bounded paging;
changing channel starts a fresh traversal. Focused assessment archive capability guarantees filtering before evidence
limits, whereas older default methods can throw or filter after a bounded read.

## Related documentation

- [Parent module](../README.md)
- [Live output](../Logging/README.md)
- [Operational telemetry](../../Diagnostics/README.md)
- [Pipeline and storage adapters](../../../Infrastructure/Diagnostics/README.md)
- [Telemetry architecture](../../../docs/saas-telemetry.md)
