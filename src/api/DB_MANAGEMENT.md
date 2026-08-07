# Database Management

## Migration-Managed Startup

PrintFarmer applies EF Core migrations during startup through
`ProviderAwareMigrationRunner`. Database migration and schema validation failures are
fatal: the API does not start against an unsafe or partial schema.

Migration projects are provider- and context-specific:

- `Farm.Migrations.PostgreSQL` and `Farm.Migrations.SqlServer` manage the core
  `AppDbContext`.
- `Farm.Slicer.Migrations.PostgreSQL` and
  `Farm.Slicer.Migrations.SqlServer` manage `SlicerDbContext`.
- The corresponding SQLite migration projects support local and embedded
  deployments.

## Legacy SQLite Adoption

SQLite databases previously created without `__EFMigrationsHistory` can be adopted
only after their schema exactly matches the expected relational model. The runner
records the ordered migration set without relying on a migration-name convention,
then applies any remaining migrations.

If no migration baseline is available or the schema fingerprint does not match,
startup fails with a `DatabaseMigrationContractException`. Back up the database
before recovery; restore a compatible or known-good backup rather than deleting the
migration history or forcing migrations against populated tables.

PostgreSQL and SQL Server databases without migration history are not adopted
automatically. Restore a migration-managed backup before upgrading.

## Adding Migrations

Run EF commands from `src/` and create migrations for every affected provider and
context pair. See the repository's `.github/copilot-instructions.md` for the current
commands and validation requirements.
