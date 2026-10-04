# Local development from a stopped environment

Use PowerShell 7 from `C:\Dev\AutoMate`. Docker Desktop must be running in Linux-container mode.
Web runs on the host, while PostgreSQL, Redis, Loki, Mimir, object storage, the telemetry disk gateway and Grafana run
in Docker.

Private settings have been synchronized in `deploy/.env`, `deploy/development/.env`,
`.telemetry/pilot/compose.env` and `.telemetry/pilot/automate.env`. They are ignored by Git.
The development import merges those values into Web user secrets so IDE launches use the same configuration.
External OAuth/email credentials, database/storage passwords, certificates and Grafana identity are preserved.

```powershell
Set-Location C:\Dev\AutoMate

./deploy/Configure-LocalDevelopment.ps1

docker compose --env-file deploy/.env -f deploy/docker-compose.yml up -d --wait --wait-timeout 90

dotnet build AutoMate.slnx

dotnet ef database update --project Infrastructure --startup-project Web --no-build

docker compose --env-file .telemetry/pilot/compose.env -f deploy/telemetry/compose.yaml -f deploy/telemetry/compose.disk.yaml -f deploy/telemetry/compose.grafana.yaml --profile telemetry --profile grafana up -d --build

dotnet run --project Web --launch-profile https --no-build
```

Alternatively, stop before the final command and run Web's `https` launch profile in the IDE.
AutoMate is at `https://localhost:7288`; Grafana is at `http://localhost:3000`.
If HTTPS is not already trusted on this machine, run `dotnet dev-certs https --trust` once.
Grafana's existing admin password is stored in `.telemetry/pilot/grafana/admin-password`.

The database update is idempotent. No new migration or EF upgrade is needed.
The host application uses `localhost:5432`; the telemetry container uses `host.docker.internal:5432`.
The importer does not print credentials. OAuth/email validity still depends on the existing provider registrations.

Stop Web with Ctrl+C or the IDE stop button. To stop the containers while retaining them and their volumes:

```powershell
docker compose --env-file .telemetry/pilot/compose.env -f deploy/telemetry/compose.yaml -f deploy/telemetry/compose.disk.yaml -f deploy/telemetry/compose.grafana.yaml --profile telemetry --profile grafana stop
docker compose --env-file deploy/.env -f deploy/docker-compose.yml stop
```

Do not run `down --volumes` or rerun telemetry-secret initialization against the existing pilot.
Following a source update, repeat build/migrations and the telemetry `up --build` command before restarting Web.
