---
name: "EF Migration Drift"
description: "Check PrintFarmer EF Core model drift against all deployment migration projects."
domain: "database-migrations"
confidence: "medium"
source: "earned from issue #409 on 2026-05-31"
---

## Context

PrintFarmer deployment databases depend on explicit EF Core migrations for both the main app and slicer contexts. A PR can compile while still leaving entity model changes missing from the latest migration, so CI must run EF Core's pending model changes check.

## Pattern

Run drift checks from `src/` after `dotnet restore` and before tests:

```bash
DB_PROVIDER=postgres dotnet ef migrations has-pending-model-changes \
  --project ./migrations/Farm.Migrations.PostgreSQL \
  --startup-project ./migrations/Farm.Migrations.PostgreSQL \
  --context AppDbContext

DB_PROVIDER=sqlserver dotnet ef migrations has-pending-model-changes \
  --project ./migrations/Farm.Migrations.SqlServer \
  --startup-project ./migrations/Farm.Migrations.SqlServer \
  --context AppDbContext

DB_PROVIDER=postgres dotnet ef migrations has-pending-model-changes \
  --project ./migrations/Farm.Slicer.Migrations.PostgreSQL \
  --startup-project ./migrations/Farm.Slicer.Migrations.PostgreSQL \
  --context SlicerDbContext

DB_PROVIDER=sqlserver dotnet ef migrations has-pending-model-changes \
  --project ./migrations/Farm.Slicer.Migrations.SqlServer \
  --startup-project ./migrations/Farm.Slicer.Migrations.SqlServer \
  --context SlicerDbContext
```

## Gotchas

- Set `DB_PROVIDER` per command; the migration projects use that environment variable to select provider-specific services.
- Name the context/provider in failure output so reviewers know which migration project needs a new migration.
- Keep checks before tests in CI to fail fast on schema drift.
