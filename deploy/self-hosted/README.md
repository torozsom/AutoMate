# Self-hosted AutoMate

1. Copy `.env.example` to `.env`, set unique database passwords and OAuth credentials, then set `PROJECTS_ROOT` to the
   only host directory AutoMate may scan and deploy. In the AutoMate UI this directory appears as `/workspace`.
2. Configure the GitHub and Microsoft OAuth callback URLs for this installation (for example
   `https://automate.example.com/signin-github` and `https://automate.example.com/signin-microsoft`).
3. Run `docker compose --env-file .env up -d`.
4. Upgrade by changing `AUTOMATE_IMAGE` to a release tag and running `docker compose pull && docker compose up -d`.

The Docker socket grants broad control over the local Docker daemon. Install this profile only on a developer-controlled
machine and do not expose it publicly without an additional access boundary.
