# Data

EF Core DbContext, mappings, token protection conversion, and migrations.

## Source inventory

- `AutoMateDbContext.cs`
- `Migrations/` — EF Core schema history, including GitHub workflow/job streaming checkpoints.

GitHub workflow checkpoints retain only state fingerprints, line counts, and redacted-content hashes needed to resume
diagnostic streaming; they never store raw GitHub Actions log content.

## Boundary

Infrastructure may reference Application and Domain, but never Web. It implements ports and owns provider-specific behavior.

## Related documentation

- [Solution navigation map](../../.agents/navigation.md)
