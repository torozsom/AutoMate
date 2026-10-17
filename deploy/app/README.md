# Local ASP.NET applications

For Web builds/debugging in the IDE at `https://localhost:7288`, use `../manage.ps1 start -Ide` (or
`bash ../manage.sh start --ide`). `compose.ide.yaml` leaves Docker Web stopped and publishes the Telemetry host on
loopback port 9450. The following instructions retain the full Docker alternative.

Start the infrastructure and telemetry stacks, then run from this folder:

```powershell
docker compose up -d
```

Open [AutoMate](http://localhost:8080). This stack builds AutoMate Web and the Telemetry ASP.NET host from the checkout.
Both run in Development mode. `.env` supplies database/Redis passwords, OAuth credentials, the ingestion token and
`PROJECTS_ROOT`; the private values are already populated on this machine.

A one-shot dependency check waits for PostgreSQL, Redis, Loki and Mimir. Web applies database migrations on startup;
Telemetry starts after Web's HTTP health check passes and spool permissions are initialized. Inspect
`docker compose ps` and `docker compose logs automate telemetry` if startup fails. Web's existing startup code logs
migration failures without stopping the process, so review startup logs when diagnosing database problems.

Telemetry alone writes the persistent spool/archive volume. Do not start another writer against that volume.
`TELEMETRY_SPOOL_VOLUME` preserves the existing archive name. Infrastructure and telemetry create the external
networks before this stack joins them; keep their project/network identities stable.

After source changes, use `docker compose up -d --build`. To stop, run `docker compose down` here before stopping
telemetry and infrastructure. Persistent volumes survive shutdown.

Web listens on loopback port 8080 by default. `PROJECTS_ROOT` appears as `/workspace` inside Web. The mounted Docker
socket enables local deployments. Authenticated telemetry ingestion remains on the private Docker networks; internal
HTTP uses the existing development-only configuration option.

See [local setup](../README.md) for first-time credentials and the complete three-command sequence.
