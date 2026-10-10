# Local telemetry and Grafana

For AutoMate platform security and AI workflow monitoring, use the separate
[operator guide](../../docs/ai-analysis-operations.md). It defines dashboard panels, safe dimensions, initial alerts,
read-only AI queue queries and incident responses. The deployment dashboards below remain deployment-history views;
platform OTLP collection, persistent trace storage and alert destinations require operator setup.

## User → project → deployment navigation

With PostgreSQL, the telemetry stack and Grafana running, use PowerShell 7:

```powershell
./deploy/telemetry/Sync-GrafanaHierarchy.ps1
```

Open [AutoMate user project deployment](http://localhost:3000/d/automate-hierarchy), also available under **Dashboards →
AutoMate**. Select **User**, then **Project**, then **Deployment**. Projects have names;
deployments have UTC creation times, short IDs and statuses. All remains inside the selected user's tenant.
Choose the dashboard time range containing the deployment's observations. CPU/memory requires runtime collection.

The script provisions fixed-tenant Loki/Mimir data sources for every current user and a PostgreSQL metadata catalog
with a dedicated read-only account. The catalog exposes only usernames, project/deployment IDs, names, times and
statuses through views; it cannot access base application tables. PostgreSQL is used for navigation only, not raw
log or metric storage. This includes projects and deployments with no metric samples. Project/deployment lists update
on dashboard reload. Rerun the script after adding users or recreating PostgreSQL; it preserves existing Grafana login
credentials and reloads provisioning through Grafana's API without restarting storage or AutoMate.

This is a private, loopback-only **operator** dashboard, not customer authorization. The operator can inspect every
provisioned tenant. No new Prometheus server or Grafana plugin is required. Database catalog changes are local operator
setup, not an EF migration. The default database endpoint is `host.docker.internal:5432`; override `-PostgresAddress`
for a different local Docker setup. The metadata connection disables database TLS for local development only.

The base `compose.yaml` hosts Loki, Mimir, SeaweedFS, and the TLS gateway. See
[storage setup and retention](../../docs/deployment-telemetry.md) for initializing the base stack.

## Configure AutoMate development

After initializing the pilot, import its complete configuration for IDE and `dotnet run` launches:

```powershell
./deploy/telemetry/Configure-TelemetryDevelopment.ps1
```

This merges the backend, all three endpoints, and gateway authorization into Web's .NET user secrets without printing
credentials. Restart AutoMate afterward. Selecting only `Backend=LokiMimir` is incomplete and fails startup validation.
Use `-SecretsDirectory` if your pilot credentials are stored elsewhere. The importer configures the pilot's public
certificate as an explicit trust root for AutoMate's telemetry HTTP client only. Certificate-chain and hostname
verification remain enabled; no system-wide certificate installation or TLS bypass is used.

## Enable the local Grafana UI

Use PowerShell 7 from the repository root. Select the AutoMate project owner's `applications.user_id`, not the GitHub
identity. The script normalizes the GUID and provisions both data sources for that one tenant:

```powershell
./deploy/telemetry/Initialize-Grafana.ps1 -TenantId '<AutoMate-user-GUID>'
docker compose --env-file .telemetry/pilot/compose.env -f deploy/telemetry/compose.yaml -f deploy/telemetry/compose.grafana.yaml --profile telemetry --profile grafana up -d
docker compose --env-file .telemetry/pilot/compose.env -f deploy/telemetry/compose.yaml --profile telemetry exec -T gateway nginx -s reload
```

Open **http://localhost:3000**, sign in as **admin**, and retrieve the generated password locally:

```powershell
Get-Content .telemetry/pilot/grafana/admin-password
```

Open **Dashboards → AutoMate → AutoMate deployments** for CPU cores, memory usage/limit, and saved output. Project and
deployment filters accept GUIDs **without hyphens**; `.*` selects all within the provisioned account. Container and log
channel filters also accept regular expressions. Set the dashboard time range to match the deployment's timestamps.

In **Explore**, select **AutoMate Loki** or **AutoMate Mimir**. Example queries:

```logql
{service_name="automate"} | channel="build"
```

```promql
automate_cpu_usage_cores
```

Memory metric names are `automate_memory_used_bytes` and `automate_memory_limit_bytes`. Runtime collection must be
enabled in AutoMate before those samples are collected. Grafana queries the stores directly; it does not merge pending
PostgreSQL outbox records or legacy PostgreSQL diagnostics. No data during pending ingestion or before opt-in is
expected.

If both data-source health checks pass but the panels are empty, confirm AutoMate was restarted after selecting
`TelemetryStorage:Backend=LokiMimir`, then start a new deployment. Existing PostgreSQL history is not backfilled into
Loki or Mimir. Enable the runtime-collection checkbox to collect CPU/memory and ongoing container logs; metrics normally
arrive on a 60-second sampling interval. Choose the time range containing those observations.

## Configuration and boundaries

- `compose.grafana.yaml` is an optional override with the `grafana` profile and loopback-only port 3000. Set
  `GRAFANA_PORT` in the Compose environment to use another local port.
- `Initialize-Grafana.ps1` reads existing telemetry credentials and the public certificate, then writes private
  provisioning and random login/encryption secrets under `.telemetry/pilot/grafana`. Rerunning preserves those secrets.
  Use `-SecretsDirectory` for another initialized telemetry directory; its `TELEMETRY_SECRETS_DIR` must match in
  Compose.
- `grafana/provisioning/dashboards.yaml` and `grafana/dashboards/automate-deployments.json` provision the dashboard.
  Local dashboard edits can be saved as separate copies; edit the checked-in JSON to update the supplied dashboard.
- Grafana keeps settings and encrypted data-source credentials in its persistent `grafana` volume. Keep the generated
  encryption key for restart recovery. Provisioned data sources are not editable through the UI.
- Grafana verifies the gateway certificate using the supplied CA and `localhost` server name. The gateway still
  requires authentication and permits only the enumerated read/query and existing ingestion routes; administrative
  storage endpoints remain blocked.
- This is a **local operator UI**, with a fixed tenant and an administrative login. Dashboard filters provide no SaaS
  authorization. Do not expose it to customers or publish its port beyond loopback. Adding SaaS access requires separate
  authentication and server-enforced tenant authorization.

To select another local tenant, rerun initialization with its owner GUID, then restart Grafana. Existing credentials
remain unchanged. This changes the account scope of the entire local operator UI, including saved dashboards.

## Deployment telemetry update

Both SelfHosted and SaaS use the private Telemetry disk gateway for new deployment logs and metrics. PostgreSQL payload
writes are rejected at application startup; legacy reads and draining of existing outbox rows remain available. The
gateway confirms durable checksummed writes before cloud checkpoints advance. Tenant-scoped v2 history, deployment
revision recovery and weighted daily project analytics are documented in [the rollout guide](/docs/saas-telemetry.md).
Detailed data expires after 30 days; daily statistics after 365 days. See the root navigation.md for new module entry
points.

For both self-hosted development and SaaS, start the disk service by also including compose.disk.yaml with --build. Set
TELEMETRY_DATABASE_CONNECTION in the private compose.env to a database hostname reachable from the telemetry container
(for Docker Desktop host development, host.docker.internal). New initialization generates the ingestion token;
Configure-TelemetryDevelopment.ps1 upgrades an older pilot's compose.env token and imports DiskGateway into Web user
secrets. Import before starting the service so an upgraded pilot has its token. The development ingestion URL is
loopback http://localhost:9450; Loki/Mimir query endpoints continue to verify their TLS certificate. See
../self-hosted/README.md for the complete self-hosted Compose command.
