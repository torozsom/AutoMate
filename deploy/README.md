# Deploy and run AutoMate

All checked-in hosting files live here. Run the commands below from the repository root.

| Location             | Purpose                                                                                   |
|----------------------|-------------------------------------------------------------------------------------------|
| `docker-compose.yml` | PostgreSQL and Redis for local development with `dotnet run`                              |
| `self-hosted/`       | Containerized AutoMate with access to a developer-controlled project directory and Docker |
| `saas/`              | Public-service hosting profile and configuration guidance                                 |
| `telemetry/`         | Optional Loki/Mimir storage and authenticated TLS gateway                                 |

## Local development

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

Telemetry credentials, certificates, and working test artifacts remain in the ignored root `.telemetry/` directory.
Its initialization script resolves that directory relative to its own location, independently of the shell's working
directory.
