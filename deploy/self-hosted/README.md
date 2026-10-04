# Self-hosted AutoMate

1. Copy `.env.example` to `.env`, set unique database passwords and OAuth credentials, then set `PROJECTS_ROOT` to the
   only host directory AutoMate may scan and deploy. In the AutoMate UI this directory appears as `/workspace`.
2. Configure the GitHub and Microsoft OAuth callback URLs for this installation (for example
   `https://automate.example.com/signin-github` and `https://automate.example.com/signin-microsoft`).
3. Run `docker compose --env-file .env up -d`.
4. Upgrade by changing `AUTOMATE_IMAGE` to a release tag and running `docker compose pull && docker compose up -d`.

The Docker socket grants broad control over the local Docker daemon. Install this profile only on a developer-controlled
machine and do not expose it publicly without an additional access boundary.

## Required deployment telemetry

Self-hosted uses the same durable disk gateway as SaaS: Web → Telemetry persistent spool → Loki/Mimir. New raw logs and
metrics never fall back to PostgreSQL, including backend outages. PostgreSQL keeps deployment metadata, collection
checkpoints and daily summaries. Historical PostgreSQL logs remain readable until expiry.

Before starting Web, apply the migrations and run one private Telemetry service with its persistent volume, the same
PostgreSQL connection and the same ingestion token. Configure all TELEMETRY_* endpoint/token values in this profile's
private .env. HTTPS is required by default; a private CA can be configured through TelemetryStorage__CaCertificatePath
with a read-only certificate mount. Missing settings prevent startup rather than selecting a PostgreSQL fallback.

For a developer-controlled local installation, the bundled override joins Telemetry to the application's PostgreSQL
network. From the repository root, initialize the telemetry pilot credentials as described in ../telemetry/README.md,
configure self-hosted/.env from its example, and set TELEMETRY_DATABASE_CONNECTION to the same PostgreSQL password. Do
not put the ingestion token in two env files: the generated pilot compose.env supplies it to both processes.

```powershell
docker compose --env-file deploy/self-hosted/.env --env-file .telemetry/pilot/compose.env -p automate-self-hosted -f deploy/telemetry/compose.yaml -f deploy/telemetry/compose.disk.yaml -f deploy/self-hosted/docker-compose.yml -f deploy/self-hosted/compose.telemetry-development.yml --profile telemetry up -d --build
```

This override explicitly permits plaintext only on private Docker networks and loopback ingestion. Use private HTTPS
endpoints for public/production installations. It runs one spool writer on a named volume; disk loss can lose pending
events. Detailed data retains 30 days, daily summaries 365 days. See ../../docs/saas-telemetry.md for migration,
retention and monitoring.
