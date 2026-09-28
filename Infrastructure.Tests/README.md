# Infrastructure Tests

Automated, credential-free verification for deployment diagnostics and external-adapter normalization.

Run from the repository root:

```powershell
dotnet test AutoMate.slnx --no-restore
```

These tests use fake HTTP responses and temporary SQLite databases. They do not contact Docker, GitHub, Azure, or an
OpenTelemetry collector.
