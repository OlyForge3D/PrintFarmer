using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Data.Migrations;

/// <summary>
/// Identifies the migration contract for a database context.
/// </summary>
/// <param name="ContextName">A non-sensitive name used in diagnostics.</param>
/// <param name="SentinelTable">A table that identifies a legacy schema owned by the context.</param>
public sealed record DatabaseMigrationTarget(string ContextName, string SentinelTable)
{
    public static DatabaseMigrationTarget Core { get; } = new("core", "Printers");

    public static DatabaseMigrationTarget Slicer { get; } = new("slicer", "SliceJobs");
}

/// <summary>
/// Describes the migration work completed during startup.
/// </summary>
/// <param name="LegacySchemaBaselined">Whether a verified legacy schema was baselined.</param>
/// <param name="AppliedMigrations">The migrations recorded after the operation.</param>
public sealed record DatabaseMigrationResult(
    bool LegacySchemaBaselined,
    IReadOnlyList<string> AppliedMigrations);

/// <summary>
/// Indicates that the database could not be safely migrated.
/// </summary>
public sealed class DatabaseMigrationContractException : InvalidOperationException
{
    public DatabaseMigrationContractException()
        : this("Database migration contract failed.")
    {
    }

    public DatabaseMigrationContractException(string message)
        : base(message)
    {
        Code = "migration_failed";
    }

    public DatabaseMigrationContractException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "migration_failed";
    }

    public DatabaseMigrationContractException(
        string code,
        DatabaseMigrationTarget target,
        string detail,
        Exception? innerException = null)
        : base(
            $"{target.ContextName} database migration stopped ({code}). {detail} " +
            "Back up the database before recovery. Restore a schema compatible with this release " +
            "or restore a known-good backup, then restart the service.",
            innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Applies migrations for all supported providers and safely adopts databases previously created
/// without migration history.
/// </summary>
public static class ProviderAwareMigrationRunner
{
    public static async Task<DatabaseMigrationResult> MigrateAsync(
        DbContext context,
        DatabaseMigrationTarget target,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(logger);

        SupportedProvider provider = GetSupportedProvider(context, target);
        string[] contextMigrations = [.. context.Database.GetMigrations()];
        if (contextMigrations.Length == 0)
        {
            throw new DatabaseMigrationContractException(
                "migration_assembly_missing",
                target,
                $"No {provider.DisplayName} migrations were found. Verify the configured migration assembly.");
        }

        try
        {
            string[] appliedMigrations = [.. await context.Database.GetAppliedMigrationsAsync(cancellationToken)];
            var contextMigrationSet = contextMigrations.ToHashSet(StringComparer.Ordinal);
            bool hasContextHistory = appliedMigrations.Any(contextMigrationSet.Contains);
            SchemaContract expectedSchema = BuildExpectedSchema(context, provider);
            bool sentinelExists = await TableExistsAsync(
                context,
                provider,
                expectedSchema.Find(target.SentinelTable),
                cancellationToken);

            bool baselined = false;
            if (!hasContextHistory && sentinelExists)
            {
                await ValidateSchemaAsync(context, provider, expectedSchema, target, cancellationToken);
                await BaselineLegacySchemaAsync(
                    context,
                    contextMigrations,
                    appliedMigrations,
                    target,
                    logger,
                    cancellationToken);
                baselined = true;
            }
            else if (!hasContextHistory && !sentinelExists &&
                     await AnyExpectedTableExistsAsync(context, provider, expectedSchema, cancellationToken))
            {
                string detail =
                    $"Some {target.ContextName} tables exist but the sentinel table " +
                    $"'{target.SentinelTable}' is missing. The service will not guess at or modify this partial schema.";
                throw new DatabaseMigrationContractException(
                    "legacy_schema_incomplete",
                    target,
                    detail);
            }

            await context.Database.MigrateAsync(cancellationToken);
            await ValidateSchemaAsync(context, provider, expectedSchema, target, cancellationToken);

            string[] finalAppliedMigrations =
            [
                .. (await context.Database.GetAppliedMigrationsAsync(cancellationToken))
                    .Where(contextMigrationSet.Contains)
            ];

            logger.LogInformation(
                "[Database] {Context} schema is migration-managed on {Provider}; {Count} migration(s) recorded",
                target.ContextName,
                provider.DisplayName,
                finalAppliedMigrations.Length);

            return new DatabaseMigrationResult(baselined, finalAppliedMigrations);
        }
        catch (DatabaseMigrationContractException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            string detail =
                $"{provider.DisplayName} could not apply or validate the migration set. " +
                "Review database permissions and migration history.";
            throw new DatabaseMigrationContractException(
                "migration_failed",
                target,
                detail,
                ex);
        }
    }

    private static async Task BaselineLegacySchemaAsync(
        DbContext context,
        string[] contextMigrations,
        string[] alreadyAppliedMigrations,
        DatabaseMigrationTarget target,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        IHistoryRepository historyRepository = context.GetService<IHistoryRepository>();
        string productVersion = typeof(DbContext).Assembly.GetName().Version?.ToString(3) ?? "10.0.0";
        var appliedSet = alreadyAppliedMigrations.ToHashSet(StringComparer.Ordinal);

        await using IDbContextTransaction transaction =
            await context.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            _ = await historyRepository.CreateIfNotExistsAsync(cancellationToken);

            foreach (string migration in contextMigrations.Where(migration => !appliedSet.Contains(migration)))
            {
                string insertScript = historyRepository.GetInsertScript(new HistoryRow(migration, productVersion));
                _ = await context.Database.ExecuteSqlRawAsync(insertScript, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        logger.LogWarning(
            "[Database] Adopted verified legacy {Context} schema into migration history with {Count} migration(s)",
            target.ContextName,
            contextMigrations.Length);
    }

    private static SchemaContract BuildExpectedSchema(DbContext context, SupportedProvider provider)
    {
        var tables = new Dictionary<TableIdentifier, HashSet<string>>();

        foreach (IEntityType entityType in context.Model.GetEntityTypes())
        {
            string? tableName = entityType.GetTableName();
            if (tableName is null)
            {
                continue;
            }

            string? schema = provider.Kind == ProviderKind.Sqlite
                ? null
                : entityType.GetSchema() ?? context.Model.GetDefaultSchema() ?? provider.DefaultSchema;
            var table = new TableIdentifier(tableName, schema);
            var storeObject = StoreObjectIdentifier.Table(tableName, schema);

            if (!tables.TryGetValue(table, out HashSet<string>? columns))
            {
                columns = new HashSet<string>(provider.IdentifierComparer);
                tables.Add(table, columns);
            }

            foreach (IProperty property in entityType.GetProperties())
            {
                string? columnName = property.GetColumnName(storeObject);
                if (columnName is not null)
                {
                    _ = columns.Add(columnName);
                }
            }
        }

        return new SchemaContract(tables);
    }

    private static async Task ValidateSchemaAsync(
        DbContext context,
        SupportedProvider provider,
        SchemaContract expectedSchema,
        DatabaseMigrationTarget target,
        CancellationToken cancellationToken)
    {
        var missingObjects = new List<string>();
        DbConnection connection = context.Database.GetDbConnection();
        bool closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            foreach ((TableIdentifier table, HashSet<string> expectedColumns) in expectedSchema.Tables)
            {
                HashSet<string> actualColumns =
                    await ReadColumnsAsync(connection, provider, table, cancellationToken);
                if (actualColumns.Count == 0)
                {
                    missingObjects.Add($"{table.DisplayName} (table)");
                    continue;
                }

                missingObjects.AddRange(
                    expectedColumns
                        .Where(column => !actualColumns.Contains(column))
                        .Select(column => $"{table.DisplayName}.{column}"));
            }
        }
        finally
        {
            if (closeConnection)
            {
                await connection.CloseAsync();
            }
        }

        if (missingObjects.Count > 0)
        {
            const int maximumReportedObjects = 20;
            string suffix = missingObjects.Count > maximumReportedObjects
                ? $" (and {missingObjects.Count - maximumReportedObjects} more)"
                : string.Empty;
            string detail =
                $"The schema is missing required objects: " +
                $"{string.Join(", ", missingObjects.Take(maximumReportedObjects))}{suffix}. " +
                "No legacy baseline was recorded.";
            throw new DatabaseMigrationContractException(
                "schema_validation_failed",
                target,
                detail);
        }
    }

    private static async Task<bool> AnyExpectedTableExistsAsync(
        DbContext context,
        SupportedProvider provider,
        SchemaContract expectedSchema,
        CancellationToken cancellationToken)
    {
        foreach (TableIdentifier table in expectedSchema.Tables.Keys)
        {
            if (await TableExistsAsync(context, provider, table, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> TableExistsAsync(
        DbContext context,
        SupportedProvider provider,
        TableIdentifier table,
        CancellationToken cancellationToken)
    {
        DbConnection connection = context.Database.GetDbConnection();
        bool closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            HashSet<string> columns = await ReadColumnsAsync(connection, provider, table, cancellationToken);
            return columns.Count > 0;
        }
        finally
        {
            if (closeConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(
        DbConnection connection,
        SupportedProvider provider,
        TableIdentifier table,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = provider.Kind switch
        {
            ProviderKind.Sqlite =>
                "SELECT name FROM pragma_table_info(@tableName)",
            ProviderKind.PostgreSql =>
                "SELECT column_name FROM information_schema.columns " +
                "WHERE table_schema = @schemaName AND table_name = @tableName",
            ProviderKind.SqlServer =>
                "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS " +
                "WHERE TABLE_SCHEMA = @schemaName AND TABLE_NAME = @tableName",
            _ => throw new InvalidOperationException("Unsupported database provider."),
        };

        AddParameter(command, "@tableName", table.Name);
        if (provider.Kind != ProviderKind.Sqlite)
        {
            AddParameter(command, "@schemaName", table.Schema ?? provider.DefaultSchema);
        }

        var columns = new HashSet<string>(provider.IdentifierComparer);
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            _ = columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);
    }

    private static SupportedProvider GetSupportedProvider(
        DbContext context,
        DatabaseMigrationTarget target)
    {
        string providerName = context.Database.ProviderName ?? string.Empty;
        if (providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return new SupportedProvider(
                ProviderKind.Sqlite,
                "SQLite",
                string.Empty,
                StringComparer.OrdinalIgnoreCase);
        }

        if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            return new SupportedProvider(
                ProviderKind.PostgreSql,
                "PostgreSQL",
                "public",
                StringComparer.Ordinal);
        }

        if (providerName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            return new SupportedProvider(
                ProviderKind.SqlServer,
                "SQL Server",
                "dbo",
                StringComparer.OrdinalIgnoreCase);
        }

        throw new DatabaseMigrationContractException(
            "provider_unsupported",
            target,
            $"Provider '{providerName}' is not supported. Supported providers are SQLite, PostgreSQL, and SQL Server.");
    }

    private enum ProviderKind
    {
        Sqlite,
        PostgreSql,
        SqlServer,
    }

    private sealed record SupportedProvider(
        ProviderKind Kind,
        string DisplayName,
        string DefaultSchema,
        StringComparer IdentifierComparer);

    private sealed record TableIdentifier(string Name, string? Schema)
    {
        public string DisplayName => string.IsNullOrWhiteSpace(Schema) ? Name : $"{Schema}.{Name}";
    }

    private sealed class SchemaContract(Dictionary<TableIdentifier, HashSet<string>> tables)
    {
        public IReadOnlyDictionary<TableIdentifier, HashSet<string>> Tables { get; } = tables;

        public TableIdentifier Find(string tableName) =>
            Tables.Keys.FirstOrDefault(table =>
                string.Equals(table.Name, tableName, StringComparison.OrdinalIgnoreCase))
            ?? new TableIdentifier(tableName, null);
    }
}
