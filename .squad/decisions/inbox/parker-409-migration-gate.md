---
date: 2026-05-31
owner: Parker
status: proposed
issue: 409
---

## EF Core Migration Drift CI Gate

PrintFarmer uses one CI gate in `.github/workflows/ci.yml` to detect EF Core entity model drift before tests run. The gate installs `dotnet-ef` after solution restore and runs `dotnet ef migrations has-pending-model-changes` from `src/`.

The gate covers all four deployment migration projects:

- `Farm.Migrations.PostgreSQL` with `AppDbContext` and `DB_PROVIDER=postgres`
- `Farm.Migrations.SqlServer` with `AppDbContext` and `DB_PROVIDER=sqlserver`
- `Farm.Slicer.Migrations.PostgreSQL` with `SlicerDbContext` and `DB_PROVIDER=postgres`
- `Farm.Slicer.Migrations.SqlServer` with `SlicerDbContext` and `DB_PROVIDER=sqlserver`

The check sits after `.NET` restore and before the test steps so migration drift fails fast with an error message naming the offending context/provider.
