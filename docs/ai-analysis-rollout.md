# Internal AI pilot acceptance record

Status: **not started; provider egress and USD spending remain disabled**. This is a reusable operator record, not
evidence of a live rollout. Complete one copy per environment/release. Use [onboarding](ai-analysis.md) for
architecture,
configuration/migrations and [operations](ai-analysis-operations.md#staged-feature-enablement-and-shutdown) for staged
promotion, signals and shutdown. Keep actual sample data, account identifiers and credential material in restricted
operator records rather than this repository.

| Record field                      | Evidence to supply                                                                                                                                         |
|-----------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Environment/release/operator/date | Exact binary and configuration revision; responsible operator and reviewer.                                                                                |
| Provider approval                 | Contract/DPA, account/project, model, region, data categories, retention/deletion and spend controls.                                                      |
| Cohort approval                   | Explicit internal owner accounts, consenting sample projects and anonymization review.                                                                     |
| Infrastructure acceptance         | Applied migration history, PostgreSQL trigger/locking results, metadata access, telemetry gateway/store readiness, backup/retention and recovery evidence. |
| Limits and budget                 | Reviewed worst-case full-request cost calculation, USD daily allowance, input/output/retry/capacity limits and provider-side caps.                         |
| Shutdown source                   | Effective reloadable flag source/precedence, replica inventory, observed reload delay and successful shutdown rehearsal.                                   |
| Promotion/rollback                | Reviewer decision, unresolved findings, next cohort size or rollback revision.                                                                             |

## Before the first provider call

Credential-free regression commands from the repository root (after dependency restore):

```powershell
dotnet test AutoMate.slnx --no-restore
node --test Web.Tests/JavaScript/telemetry-chart.test.cjs Web.Tests/JavaScript/xterm-wrapper.test.cjs
```

The previous implementation validation passed 584 .NET tests and six JavaScript tests, with eleven expected external/
Docker/PostgreSQL skips. That result is not an environment acceptance record or a live quality measurement. Consult
[Infrastructure test requirements](../Infrastructure.Tests/README.md) before configuring isolated integration targets.
The [owned-container PostgreSQL runner](../deploy/verification/README.md) now passes six real migration/locking/trigger
checks locally. Default suites have sixteen opt-in skips after adding these cases; production acceptance and live
model evaluation remain separate.

- [ ] Record all provider/account/model/region/data-processing approvals; verify data controls for the actual account.
- [ ] Review anonymized failing and successful Docker/GitHub/Azure sample deployments as applicable to the environment;
  confirm source coverage/omissions and redaction with synthetic secrets and injection attempts.
- [ ] Stop old workers, review/apply ordered metadata migrations and verify failure-trigger installation on isolated
  PostgreSQL before environment installation. Exercise actual cross-connection admission, reservations and rollback
  with the PostgreSQL EF provider, not just SQLite. The disposable runner covers local migration/locking semantics;
  repeat environment-specific installation/permissions acceptance. Confirm no diagnostic payload fallback into
  PostgreSQL.
- [ ] Run the documented credential-free suites and external-stack checks available to the environment. Record any
  skips rather than treating them as passing production validation.
- [ ] Verify restricted credentials, current exact-project consent and the small approved-owner cohort. Verify foreign
  owners, non-cohort projects and revoked consent are denied. Confirm global automatic analysis remains off.
- [ ] Configure accurate positive USD budget/reservation bounds and provider-side spend controls; verify quota/rate/
  capacity/spend denials show fixed Skipped guidance and safe audits without provider transmission.
- [ ] Wire platform OTLP monitoring and alert routing; verify backend mappings, volume gates and queue snapshots.
- [ ] Rehearse per-replica effective-policy shutdown using synthetic transport first, including headers/body
  cancellation,
  no retry and new-call denial. Verify deployments/diagnostics and authorized result management remain operational.

## Manual pilot observations

For each approved sample, record a restricted fixture identifier, source/coverage, expected failure or known successful
state, selected model/prompt/schema, useful/incorrect/unsupported recommendations, evidence correctness, redaction
findings, latency/error/retries, reserved allowance and actual provider usage. Record sample count and denominators for
rates. Review advice manually; do not execute suggested changes automatically or treat successful schema validation as
quality acceptance. Do not export sample diagnostic text or owner IDs as metric labels.

- [ ] Assess known failures for useful evidence-grounded advice and missing/incorrect remediation.
- [ ] Assess successful/ambiguous fixtures for false-positive diagnosis and unsupported evidence claims.
- [ ] Check displayed evidence against supplied context and note retention/source gaps separately from model errors.
- [ ] Review redacted input and validated output under restricted access; investigate any unexpected data transmission.
- [ ] Compare provider latency/error/retry behavior and quota/capacity denial trends with the agreed pilot criteria.
- [ ] Reconcile conservative reservations with provider usage, including failed/canceled/uncertain requests.
- [ ] Exercise live shutdown only within the approved pilot scope; record replica propagation and remote-charge limits.
- [ ] Record owner feedback and reviewer approval before promotion; leave unresolved findings explicit.

Pilot quality/latency/false-positive acceptance thresholds must be agreed and recorded **before** the pilot; this
repository has no measured baseline and does not invent one. Initial monitoring thresholds in the operations guide
are starting points, not quality or production SLO acceptance.

## Automatic pilot and expansion

- [ ] Approve manual results and the chosen thresholds, then enable automatic analysis only for the existing cohort.
- [ ] Verify newly persisted failures produce at most one admitted job and repeated failure notifications/result
  deletion do not duplicate it. Verify historical failures are not backfilled.
- [ ] Repeat quality/redaction/latency/cost/feedback review under automatic load, including outage/restart recovery.
- [ ] Approve each additional owner batch explicitly; record consent, budget/capacity headroom and observation period.
- [ ] On a failed gate, disable egress on every replica, reconcile remote usage and retain safe incident evidence.
  Resume through the reviewed manual stage after remediation; Skipped results are not automatically replayed.

M9's live-rollout checkbox can be completed only when actual pilot execution and review evidence exist. Documentation,
unit tests, a healthy readiness probe or enabling flags alone do not satisfy it.
