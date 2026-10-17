# Deploy and run AutoMate

All checked-in hosting files live here. Run the commands below from the repository root.

## Three-folder self-hosted setup

Each folder has a default Compose file, so normal startup requires only `docker compose up -d` inside each folder:

| Folder            | Starts                                                                              |
|-------------------|-------------------------------------------------------------------------------------|
| `infrastructure/` | PostgreSQL and Redis                                                                |
| `telemetry/`      | SeaweedFS object storage, Loki, Mimir and the TLS query gateway                     |
| `app/`            | AutoMate Web and the Telemetry ASP.NET host, including its persistent spool/archive |

For first-time configuration, copy `infrastructure/.env.example` and `app/.env.example` to `.env` in their respective
folders. Choose matching database/Redis passwords, set OAuth credentials and `PROJECTS_ROOT` in `app/.env`, and
initialize telemetry secrets once from the repository root:

```powershell
./deploy/telemetry/Initialize-TelemetrySecrets.ps1
Copy-Item .telemetry/pilot/compose.env deploy/telemetry/.env
```

If pilot secrets already exist, reuse them and copy their `compose.env`; do not rerun initialization or replace storage
credentials. Copy its `TELEMETRY_INGEST_TOKEN` value to `app/.env`. All `.env` files remain private and ignored by Git.

Start the stacks in order (commands below begin at the repository root):

```powershell
cd deploy/infrastructure
docker compose up -d
docker compose ps
cd ../telemetry
docker compose up -d
cd ../app
docker compose up -d
```

Wait for PostgreSQL/Redis to become healthy and for Loki/Mimir to finish starting before starting app. Open
[AutoMate](http://localhost:8080). The app stack builds both ASP.NET images on first start; after source changes use
`docker compose up -d --build` there. Stop with `docker compose down` in each folder, in reverse order. Named volumes
survive shutdown. Read [app setup and migration notes](app/README.md) before switching an existing installation.

The following options retain the earlier IDE-development and combined self-hosted workflows.

| Location             | Purpose                                                                                   |
|----------------------|-------------------------------------------------------------------------------------------|
| `docker-compose.yml` | PostgreSQL and Redis for local development with `dotnet run`                              |
| `self-hosted/`       | Containerized AutoMate with access to a developer-controlled project directory and Docker |
| `saas/`              | Public-service hosting profile and configuration guidance                                 |
| `telemetry/`         | Required Loki/Mimir storage, durable disk gateway and authenticated TLS access            |

## Local development

For the complete cold-start sequence, including the required disk gateway and Grafana,
see [local development](../docs/local-development.md). Development telemetry endpoints are saved in
`Web/appsettings.Development.json`; private application settings persist in Web's .NET user secrets and load
automatically for Development IDE/`dotnet run` launches. `Configure-LocalDevelopment.ps1` is an optional one-time
import for a new machine or deliberately changed private settings, not a normal startup step. ASP.NET Core does not
load `.env` files automatically; Docker Compose still uses its existing private `--env-file` files.

Copy `deploy/.env.example` to `deploy/.env` if you do not already have local credentials. Configure `DB_USER` and
`DB_PASSWORD`, then start the dependencies:

```powershell
docker compose --env-file deploy/.env -f deploy/docker-compose.yml up -d
dotnet run --project Web
```

Configure AutoMate's connection strings through .NET user secrets or environment variables: PostgreSQL uses
`localhost:5432`, database `automate_db`, and the credentials in `deploy/.env`; Redis uses `localhost:6379`.
See the [application configuration instructions](../README.md#2-configure-secrets) for OAuth and other required
settings.

Stop the development dependencies:

```powershell
docker compose --env-file deploy/.env -f deploy/docker-compose.yml down
```

The development Compose project retains its original `docker` identity, explicit container names, and named volumes
(`automate_pgdata` and `automate_redis_data`). Moving these files does not migrate or reset database contents. Local
credentials moved from `.docker/.env` remain private in `deploy/.env`; only `.env.example` files are tracked.

## Other hosting options

- [Self-hosted setup](self-hosted/README.md)
- [SaaS setup](saas/README.md)
- [Telemetry setup and retention operations](../docs/deployment-telemetry.md)
- [Local Grafana dashboards and log search](telemetry/README.md)
- [Owned disposable PostgreSQL verification](verification/README.md)

Telemetry credentials, certificates, and working test artifacts remain in the ignored root `.telemetry/` directory.
Its initialization script resolves that directory relative to its own location, independently of the shell's working
directory.

Deployment telemetry is required in both hosting profiles, including local Web development. Configure the DiskGateway
and start the separate Telemetry service before starting Web; the application does not select raw PostgreSQL persistence
when configuration or storage is unavailable. See self-hosted/README.md and ../docs/saas-telemetry.md.
