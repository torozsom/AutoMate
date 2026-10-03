# SaaS cloud deployment operations

AutoMate's `SaaS` hosting profile uses PostgreSQL for durable cloud run admission. The launch worker owns a database
lease only through Azure setup and the GitHub commit. A signed GitHub App `workflow_run` webhook or the reconciler
finishes monitoring. The self-hosted profile keeps its in-process Docker and cloud deployment scheduler.

## Required configuration

- Set `HostingProfile:Mode=SaaS`, a PostgreSQL `DefaultConnection`, and the existing Microsoft OAuth configuration.
- Register a GitHub App and set `GitHubApp:AppId`, `GitHubApp:AppSlug`, `GitHubApp:PrivateKeyPem`, and
  `GitHubApp:WebhookSecret` through a managed secret provider or secret mount. Configure its webhook URL as
  `https://<host>/api/github/app/webhook`; subscribe to `workflow_run`. Grant repository **Contents: write**,
  **Workflows: write**, **Actions: read**, and **Secrets: write** only for selected repositories. Keep **Metadata:
  read**. The App key and webhook secret must never be committed to configuration files.
- Mount a PFX certificate on every SaaS instance and set `SaaS:DataProtectionCertificatePath` and
  `SaaS:DataProtectionCertificatePassword` from the secret provider. Share the same certificate across instances and
  retain old private keys during certificate rotation while any protected user tokens or run snapshots remain in use.
  Store PostgreSQL and backups encrypted, restrict access, and use TLS for database and provider connections.
- `CloudSaas` defaults to 2 active launches and 10 queued runs per user, 4 active launches per GitHub installation,
  32 active launches globally, and 8 launch tasks per process. Tune these settings from queue age, launch latency,
  GitHub throttling, Azure failures, and database load. Limits are enforced by the database across instances.

Every instance samples cluster queue depth, active launch leases, oldest queued age, pending webhook receipts, and
reconciliation backlog from PostgreSQL every 30 seconds. These gauges represent the same cluster state on each
instance; aggregate them with `max`, not `sum`. Scale launch-worker replicas from eligible queue age and p95 launch
duration within an operator-set replica maximum. Suppress scale-up while provider cooldowns are active, and alert on
webhook lag, lease recovery, failed runs, and database saturation.

## Customer setup

The customer connects GitHub and Azure, installs the AutoMate GitHub App on the chosen repository, and enters a
`<name>.azurecr.io` registry server in the deployment form. AutoMate ensures a Basic ACR with that name exists in the
selected resource group and subscription, creating it when absent. This creates a customer Azure charge. The connected
Azure account needs rights to create the resource group/registry/managed identities and assign roles. AutoMate grants
the workflow identity `AcrPush` and a separate Container App identity `AcrPull` on that registry. The generated workflow
pushes by Azure OIDC; no GitHub token is stored as a registry pull secret.

Existing GHCR deployments remain on the self-hosted path. A SaaS user must install the App and choose an ACR before
their next deployment. Do not switch an existing user to SaaS mode without a working App installation, registry,
shared Data Protection certificate, and completed migrations.

## Reliability and data policy

`cloud_deployment_runs` owns phase, lease, correlation, and protected configuration. `cloud_run_outbox` is inserted in
the same transaction as admission. The worker writes an `AutoMate-Run` marker in the Git commit and checks recent
branch history before a retry after an uncertain result. A run-scoped PostgreSQL advisory lock serializes the remote
commit check and push across lease recovery, and a stale lease owner cannot push. If it cannot verify the prior commit,
it fails safely
rather than pushing a possible duplicate. Webhooks store only verified correlation metadata in
`cloud_webhook_deliveries`; redelivery IDs are unique. The monitor resumes from persisted GitHub diagnostic checkpoints.

Only centrally redacted diagnostics enter the terminal history and its 30-day retention path. The encrypted run
snapshot can contain customer environment values, so protect database access and backups as credential-bearing data.
Terminal runs clear that snapshot when they finish; webhook receipts and completed outbox entries are removed after
30 days. Run status and commit metadata remain for project history.
Never log snapshots, provider tokens, webhook bodies, or generated secret values. Review queue age, lease recovery,
webhook lag, retry counts, installation cooldowns, and failed runs in operational telemetry. Provider credentials and
the customer ACR must be configured in an external test environment before live end-to-end validation.
