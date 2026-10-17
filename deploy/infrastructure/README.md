# Local PostgreSQL and Redis

Run from this folder:

```powershell
docker compose up -d
```

`.env` already contains this machine's PostgreSQL and Redis passwords. On a new machine, copy `.env.example` to
`.env` and choose matching passwords in `../app/.env`.

PostgreSQL 18 and password-protected Redis use persistent volumes and health checks. Neither database publishes a
host port. The app stack connects over the private Docker network and waits for dependencies before starting Web.
Start this stack before telemetry and app. Use `docker compose ps` to inspect database health.

The Docker project is named `automate-infrastructure`; existing volumes are explicitly reused to retain local data. Stop
app first, then run
`docker compose down` here. Volumes survive shutdown; `down -v` deletes data.
