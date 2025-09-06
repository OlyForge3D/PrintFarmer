# Database Management for PrintFarmer MVP

## Current Approach: EnsureCreated for Development

For the MVP phase, we're using `EnsureCreated()` instead of migrations to simplify schema changes during rapid development.

> NOTE (2025-09-06): A temporary initial EF Core migration (`InitialCreate`) was generated for evaluation and then removed per project direction. We have reverted to the pure `EnsureCreated()` workflow (deleted `Data/Migrations/` and recreated the SQLite database). Continue treating the schema as disposable during active development.

### Benefits:
- Faster iteration on schema changes
- No need to manage migration files
- Schema automatically matches entity definitions
- Simplifies development workflow

### How It Works:
- The `DatabaseInitializer` service calls `EnsureCreated()` on startup
- Any schema changes are applied automatically when the application restarts
- Original migrations are archived in `Migrations_Archive` folder
- If a migration folder appears unintentionally, delete it and remove the SQLite file (`farm.db*`) to realign with the current entity model

### Important Notes:
1. This approach is suitable for development only
2. **Data will be lost when schema changes**
3. Before deploying to production, we'll need to switch back to migrations

## Returning to Migrations for Production

When ready for production deployment:

1. Generate a consolidated migration:
   ```
   dotnet ef migrations add ProductionSchema --project src/api/Farm.Web.Api.csproj
   ```

2. Update the `DatabaseInitializer` to use `MigrateAsync()` instead of `EnsureCreatedAsync()`

3. Test the migration process thoroughly before deployment
