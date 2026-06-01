## EF migration add pattern

Use this when adding AppDbContext entities or changing EF model shape.

1. Inspect `src/infra/Data/AppDbContext.cs` and any existing `src/infra/Data/Configurations/*Configuration.cs` before editing. Confirm actual key types from existing entities instead of trusting issue sketches.
2. Put domain entities under `src/infra/Domain` and fluent configuration under `src/infra/Data/Configurations` when following current PrintFarmer infrastructure patterns.
3. Generate both main-app provider migrations from `src/`:
   - `DB_PROVIDER=postgres dotnet ef migrations add <Name> --project ./migrations/Farm.Migrations.PostgreSQL --startup-project ./migrations/Farm.Migrations.PostgreSQL --context AppDbContext`
   - `DB_PROVIDER=sqlserver dotnet ef migrations add <Name> --project ./migrations/Farm.Migrations.SqlServer --startup-project ./migrations/Farm.Migrations.SqlServer --context AppDbContext`
4. Verify the new migration files and both `AppDbContextModelSnapshot.cs` files. Be especially careful not to carry unrelated snapshot changes from concurrent migrations.
5. Run provider drift checks before review:
   - `DB_PROVIDER=postgres dotnet ef migrations has-pending-model-changes --project ./migrations/Farm.Migrations.PostgreSQL --startup-project ./migrations/Farm.Migrations.PostgreSQL --context AppDbContext`
   - `DB_PROVIDER=sqlserver dotnet ef migrations has-pending-model-changes --project ./migrations/Farm.Migrations.SqlServer --startup-project ./migrations/Farm.Migrations.SqlServer --context AppDbContext`
