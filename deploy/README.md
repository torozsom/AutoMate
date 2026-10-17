# Run AutoMate locally

## Preferred workflow: Web in the IDE

Start only Web's Docker dependencies from the repository root:

```powershell
./deploy/manage.ps1 start -Ide
```

Or use `bash deploy/manage.sh start --ide`. In the IDE, select the **Web** project and its **https** launch profile,
then build/run normally. Web opens at **https://localhost:7288**. Its existing trusted development certificate and
.NET user secrets provide HTTPS and local credentials. Web applies migrations during startup.

IDE mode adds loopback ports 5432 (PostgreSQL), 6379 (Redis) and 9450 (Telemetry). It stops the Docker Web container,
then runs the persistent Telemetry host without depending on Docker Web. The base Docker Compose files remain
available unchanged. Only run one Web instance to avoid duplicate deployment workers.

GitHub must register `https://localhost:7288/signin-github` as a callback for the configured OAuth app. Use
`https://localhost:7288` as the homepage. If retaining the Docker login option, keep its HTTP callback as an additional
registered URI. Microsoft sign-in likewise needs `https://localhost:7288/signin-microsoft` registered when used.

Stop Web through the IDE. Stop its Docker dependencies with `./deploy/manage.ps1 stop -Ide` or
`bash deploy/manage.sh stop --ide`. Use the same IDE flag with `restart`, `status`, `pull` or `down`. The scripts do
not launch or terminate an IDE-owned process. Starting without the IDE flag selects the full Docker Web workflow
below; stop IDE Web before doing so.

IDE connection strings must use `localhost`, database `automate`, and the passwords from the current infrastructure
`.env`. The gateway URL is `http://localhost:9450`; storage queries use the existing scoped CA and authorization at
`https://localhost:9443`. This machine's Web user secrets have been synchronized to those settings without changing
unrelated options. ASP.NET does not load the Compose `.env` files automatically.

## One-command control

From this folder, choose PowerShell or Bash:

| Action | PowerShell | Bash |
|--------|------------|------|
| Start everything | `./manage.ps1 start` | `bash manage.sh start` |
| Stop everything | `./manage.ps1 stop` | `bash manage.sh stop` |
| Restart everything | `./manage.ps1 restart` | `bash manage.sh restart` |
| Show all containers | `./manage.ps1 status` | `bash manage.sh status` |
| Pull registry images | `./manage.ps1 pull` | `bash manage.sh pull` |
| Rebuild apps and start | `./manage.ps1 start -Build` | `bash manage.sh start --build` |
| Remove containers, keep data | `./manage.ps1 down` | `bash manage.sh down` |

From the repository root, use `./deploy/manage.ps1 start` or `bash deploy/manage.sh start`. Both scripts also work
from other working directories. Add `-DryRun` in PowerShell or `--dry-run` in Bash to preview the commands.
Startup runs infrastructure, telemetry, then app; shutdown uses the reverse order. The app's existing readiness
checks wait for its dependencies. Scripts stop immediately if any Compose command fails and never delete volumes.
`pull` updates registry images; use the rebuild option to refresh the source-built ASP.NET images.

The wrappers keep the three Docker project names. A Dockerfile builds an image, and a combined Compose file would
use one project identity, so neither is used as a multi-project controller here.

## Individual projects

Docker Desktop must be running with Linux containers. Start these three stacks in order from the repository root:

```powershell
cd deploy/infrastructure
docker compose up -d
cd ../telemetry
docker compose up -d
cd ../app
docker compose up -d
```

Open [AutoMate](http://localhost:8080). Each command automatically loads the `.env` in its folder. The local private
files are already populated; no import script, profile flag, extra Compose file or manual database migration is
required for normal startup.

| Folder | Starts |
|--------|--------|
| [infrastructure](infrastructure/README.md) | PostgreSQL and Redis |
| [telemetry](telemetry/README.md) | SeaweedFS object storage, Loki, Mimir and the TLS query gateway |
| [app](app/README.md) | AutoMate Web and the Telemetry ASP.NET host, with persistent spool/archive |

The app stack waits for PostgreSQL, Redis, Loki and Mimir before starting Web. Web applies database migrations;
Telemetry starts after Web's HTTP health check passes. Both ASP.NET hosts run in Development mode. The first app
startup builds the images from this checkout. After source changes, run `docker compose up -d --build` in `app`.
Use `docker compose ps` and `docker compose logs` in the relevant folder to inspect startup.

Stop the stacks with `docker compose down` in each folder, in reverse order: app, telemetry, infrastructure.
Named volumes survive shutdown. Do not add `-v` unless you intend to delete persistent data. Existing volume
identities are retained so this cleanup does not reset databases or archives. Docker Desktop groups are named `automate-app`, `automate-telemetry` and `automate-infrastructure`. Do not run an older Telemetry container
against the same spool volume.

## First-time setup on another machine

Copy `infrastructure/.env.example` and `app/.env.example` to `.env` in their respective folders. Set matching
PostgreSQL/Redis passwords, OAuth credentials and the host project directory in `app/.env`. Register OAuth callbacks
for the local application's URL. Initialize telemetry credentials once from the repository root:

```powershell
./deploy/telemetry/Initialize-TelemetrySecrets.ps1
Copy-Item .telemetry/pilot/compose.env deploy/telemetry/.env
```

Copy the generated `TELEMETRY_INGEST_TOKEN` to `app/.env`. Reuse existing pilot credentials and certificates when
present; do not reinitialize storage keys. All `.env` files and `.telemetry/` artifacts are ignored by Git. Web's image
build context excludes these private files.

The Docker socket lets AutoMate manage local containers. `PROJECTS_ROOT` selects the host directory exposed as
`/workspace` inside Web. Web binds to loopback, database ports stay inside Docker, and authenticated Telemetry ingestion
uses the private Docker network. This setup is intended for local development.

[Optional Grafana tools](telemetry/README.md) and [isolated PostgreSQL verification](verification/README.md) are
available separately and are not required to start AutoMate.

The exited `dependencies`, `spool-permissions` and `permissions` containers are one-time readiness/ownership
helpers, not duplicate databases or application instances. Compose retains them to record successful initialization.
Only project names use the `automate-` prefix. Containers have explicit names such as `postgres`, `redis`, `loki`, `web` and `telemetry`, with no prefix or replica-number suffix. Registry
images use current-release tags and `pull_policy: always`; each normal startup checks for updated images. PostgreSQL
tracks the current 18.x release line so a future major release cannot open an incompatible database directory.
Both ASP.NET images are built locally; rebuild with `docker compose up -d --build` after source updates. Builds pull
the current .NET 10 SDK/runtime bases. PostgreSQL 16 data was backed up and restored into the PostgreSQL 18 volume;
the original database/Redis volumes remain available for recovery. Private database backups live in `.telemetry/backups`.
