using System.Data;
using System.Data.Common;
using System.Text;
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
        string[] contextMigrations =
        [
            .. context.Database.GetMigrations()
                .OrderBy(migration => migration, StringComparer.Ordinal)
        ];
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
                if (provider.Kind != ProviderKind.Sqlite)
                {
                    string adoptionError =
                        $"{provider.DisplayName} databases without migration history cannot be safely adopted. " +
                        "Restore a migration-managed backup before upgrading.";
                    throw new DatabaseMigrationContractException(
                        "legacy_schema_adoption_unsupported",
                        target,
                        adoptionError);
                }

                await ValidateSchemaAsync(
                    context,
                    provider,
                    expectedSchema,
                    target,
                    requireExactRelationalFingerprint: true,
                    cancellationToken);
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
            await ValidateSchemaAsync(
                context,
                provider,
                expectedSchema,
                target,
                requireExactRelationalFingerprint: false,
                cancellationToken);

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
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        logger.LogWarning(
            "[Database] Adopted verified legacy {Context} schema into migration history with {Count} migration(s)",
            target.ContextName,
            contextMigrations.Length);
    }

    private static SchemaContract BuildExpectedSchema(DbContext context, SupportedProvider provider)
    {
        IModel designTimeModel = context.GetService<IDesignTimeModel>().Model;
        string defaultCollation = NormalizeCollation(designTimeModel.GetCollation());
        var tables = new Dictionary<TableIdentifier, TableContract>();
        foreach (ITable relationalTable in designTimeModel.GetRelationalModel().Tables)
        {
            if (relationalTable.IsExcludedFromMigrations)
            {
                continue;
            }

            StoreObjectIdentifier storeObject =
                StoreObjectIdentifier.Table(relationalTable.Name, relationalTable.Schema);
            string? schema = provider.Kind == ProviderKind.Sqlite
                ? null
                : relationalTable.Schema ?? designTimeModel.GetDefaultSchema() ?? provider.DefaultSchema;
            var table = new TableIdentifier(relationalTable.Name, schema);
            var primaryKeyOrdinals = relationalTable.PrimaryKey?.Columns
                .Select((column, index) => (column.Name, Ordinal: index + 1))
                .ToDictionary(item => item.Name, item => item.Ordinal, provider.IdentifierComparer)
                ?? new Dictionary<string, int>(provider.IdentifierComparer);
            Dictionary<string, ColumnContract> columns = relationalTable.Columns
                .ToDictionary(
                    column => column.Name,
                    column => new ColumnContract(
                        column.StoreType,
                        column.IsNullable,
                        GetDefaultSql(column),
                        primaryKeyOrdinals.GetValueOrDefault(column.Name),
                        IsExpectedSqliteAutoIncrement(provider, column, primaryKeyOrdinals.ContainsKey(column.Name))),
                    provider.IdentifierComparer);
            HashSet<IndexContract> indexes =
            [
                .. relationalTable.Indexes.Select(index => new IndexContract(
                    NormalizeIdentifier(index.Name),
                    NormalizeIdentifiers(index.Columns.Select(column => column.Name)),
                    NormalizeIndexSortOrders(index.Columns.Count, index.IsDescending),
                    NormalizeIndexCollations(index.Columns, storeObject, defaultCollation),
                    index.IsUnique,
                    NormalizeSql(index.Filter),
                    SqliteIndexSource.Explicit)),
                .. relationalTable.UniqueConstraints
                    .Where(constraint => constraint != relationalTable.PrimaryKey)
                    .Select(constraint => new IndexContract(
                        null,
                        NormalizeIdentifiers(constraint.Columns.Select(column => column.Name)),
                        NormalizeIndexSortOrders(constraint.Columns.Count, null),
                        NormalizeIndexCollations(constraint.Columns, storeObject, defaultCollation),
                        true,
                        null,
                        SqliteIndexSource.UniqueConstraint)),
                .. provider.Kind == ProviderKind.Sqlite
                    ? BuildExpectedSqlitePrimaryKeyIndexes(
                        relationalTable,
                        storeObject,
                        defaultCollation)
                    : [],
            ];
            HashSet<ForeignKeyContract> foreignKeys =
            [
                .. relationalTable.ForeignKeyConstraints.Select(foreignKey => new ForeignKeyContract(
                    NormalizeIdentifier(foreignKey.PrincipalTable.Name),
                    NormalizeIdentifiers(foreignKey.Columns.Select(column => column.Name)),
                    NormalizeIdentifiers(foreignKey.PrincipalColumns.Select(column => column.Name)),
                    "NO ACTION",
                    NormalizeReferentialAction(foreignKey.OnDeleteAction))),
            ];
            HashSet<CheckConstraintContract> checkConstraints =
            [
                .. relationalTable.CheckConstraints.Select(checkConstraint =>
                    new CheckConstraintContract(NormalizeSql(checkConstraint.Sql) ?? string.Empty)),
            ];
            tables.Add(table, new TableContract(columns, indexes, foreignKeys, checkConstraints));
        }

        return new SchemaContract(tables);
    }

    private static bool IsExpectedSqliteAutoIncrement(
        SupportedProvider provider,
        IColumn column,
        bool isPrimaryKeyColumn) =>
        provider.Kind == ProviderKind.Sqlite &&
        isPrimaryKeyColumn &&
        string.Equals(NormalizeStoreType(column.StoreType), "INTEGER", StringComparison.Ordinal) &&
        column.PropertyMappings.Any(mapping =>
            mapping.Property.ValueGenerated == ValueGenerated.OnAdd);

    private static IEnumerable<IndexContract> BuildExpectedSqlitePrimaryKeyIndexes(
        ITable table,
        StoreObjectIdentifier storeObject,
        string defaultCollation)
    {
        IUniqueConstraint? primaryKey = table.PrimaryKey;
        if (primaryKey is null ||
            (primaryKey.Columns.Count == 1 &&
             NormalizeStoreType(primaryKey.Columns[0].StoreType) == "INTEGER"))
        {
            return [];
        }

        return
        [
            new IndexContract(
                null,
                NormalizeIdentifiers(primaryKey.Columns.Select(column => column.Name)),
                NormalizeIndexSortOrders(primaryKey.Columns.Count, null),
                NormalizeIndexCollations(primaryKey.Columns, storeObject, defaultCollation),
                true,
                null,
                SqliteIndexSource.PrimaryKey),
        ];
    }

    private static async Task ValidateSchemaAsync(
        DbContext context,
        SupportedProvider provider,
        SchemaContract expectedSchema,
        DatabaseMigrationTarget target,
        bool requireExactRelationalFingerprint,
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
            foreach ((TableIdentifier table, TableContract expectedTable) in expectedSchema.Tables)
            {
                if (provider.Kind == ProviderKind.Sqlite)
                {
                    SqliteTableContract? actualTable =
                        await ReadSqliteTableContractAsync(connection, table, cancellationToken);
                    if (actualTable is null)
                    {
                        missingObjects.Add($"{table.DisplayName} (table)");
                        continue;
                    }

                    if (requireExactRelationalFingerprint)
                    {
                        ValidateSqliteTable(table, expectedTable, actualTable, missingObjects);
                    }
                    else
                    {
                        missingObjects.AddRange(
                            expectedTable.Columns.Keys
                                .Where(column => !actualTable.Columns.ContainsKey(column))
                                .Select(column => $"{table.DisplayName}.{column}"));
                    }

                    continue;
                }

                HashSet<string> actualColumns =
                    await ReadColumnsAsync(connection, provider, table, cancellationToken);
                if (actualColumns.Count == 0)
                {
                    missingObjects.Add($"{table.DisplayName} (table)");
                    continue;
                }

                missingObjects.AddRange(
                    expectedTable.Columns.Keys
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

    private static async Task<SqliteTableContract?> ReadSqliteTableContractAsync(
        DbConnection connection,
        TableIdentifier table,
        CancellationToken cancellationToken)
    {
        string? createTableSql = await ReadSqliteCreateTableSqlAsync(
            connection,
            table,
            cancellationToken);
        bool hasAutoIncrement = createTableSql is not null &&
                                ContainsSqlKeyword(createTableSql, "AUTOINCREMENT");
        var columns = new Dictionary<string, ColumnContract>(StringComparer.OrdinalIgnoreCase);
        var rawColumns = new List<(string Name, string StoreType, bool IsNullable, string? DefaultSql, int PrimaryKeyOrdinal)>();
        await using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info(@tableName) ORDER BY cid";
            AddParameter(command, "@tableName", table.Name);
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string name = reader.GetString(0);
                int primaryKeyOrdinal = reader.GetInt32(4);
                string? defaultSql = await reader.IsDBNullAsync(3, cancellationToken)
                    ? null
                    : NormalizeSql(reader.GetString(3));
                rawColumns.Add((
                    name,
                    reader.GetString(1),
                    reader.GetInt32(2) == 0,
                    defaultSql,
                    primaryKeyOrdinal));
            }
        }

        if (rawColumns.Count == 0)
        {
            return null;
        }

        var primaryKeyColumns = rawColumns
            .Where(column => column.PrimaryKeyOrdinal > 0)
            .ToArray();
        bool isIntegerRowIdAlias = primaryKeyColumns.Length == 1 &&
                                   string.Equals(
                                       NormalizeStoreType(primaryKeyColumns[0].StoreType),
                                       "INTEGER",
                                       StringComparison.Ordinal);
        foreach ((string name, string storeType, bool isNullable, string? defaultSql, int primaryKeyOrdinal)
                 in rawColumns)
        {
            bool isRowIdAlias = isIntegerRowIdAlias &&
                                primaryKeyOrdinal > 0;
            columns.Add(
                name,
                new ColumnContract(
                    storeType,
                    isNullable && !isRowIdAlias,
                    defaultSql,
                    primaryKeyOrdinal,
                    isRowIdAlias && hasAutoIncrement));
        }

        HashSet<IndexContract> indexes = await ReadSqliteIndexesAsync(
            connection,
            table,
            cancellationToken);
        HashSet<ForeignKeyContract> foreignKeys = await ReadSqliteForeignKeysAsync(
            connection,
            table,
            cancellationToken);
        HashSet<CheckConstraintContract> checkConstraints = createTableSql is null
            ? []
            : ExtractSqliteCheckConstraints(createTableSql);
        return new SqliteTableContract(columns, indexes, foreignKeys, checkConstraints);
    }

    private static async Task<HashSet<IndexContract>> ReadSqliteIndexesAsync(
        DbConnection connection,
        TableIdentifier table,
        CancellationToken cancellationToken)
    {
        var indexMetadata = new List<(string Name, bool IsUnique, SqliteIndexSource Source)>();
        await using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT name, \"unique\", origin FROM pragma_index_list(@tableName)";
            AddParameter(command, "@tableName", table.Name);
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string name = reader.GetString(0);
                string origin = reader.GetString(2);
                indexMetadata.Add((
                    name,
                    reader.GetInt32(1) == 1,
                    origin.Equals("pk", StringComparison.OrdinalIgnoreCase)
                        ? SqliteIndexSource.PrimaryKey
                        : origin.Equals("u", StringComparison.OrdinalIgnoreCase)
                            ? SqliteIndexSource.UniqueConstraint
                            : SqliteIndexSource.Explicit));
            }
        }

        var indexes = new HashSet<IndexContract>();
        foreach ((string name, bool isUnique, SqliteIndexSource source) in indexMetadata)
        {
            string? filter = await ReadSqliteIndexFilterAsync(connection, name, cancellationToken);
            var columns = new List<string>();
            var sortOrders = new List<string>();
            var collations = new List<string>();
            await using DbCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT name, "desc", coll
                FROM pragma_index_xinfo(@indexName)
                WHERE key = 1
                ORDER BY seqno
                """;
            AddParameter(command, "@indexName", name);
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                bool isExpression = await reader.IsDBNullAsync(0, cancellationToken);
                bool hasCollation = !await reader.IsDBNullAsync(2, cancellationToken);
                columns.Add(isExpression ? "<expression>" : reader.GetString(0));
                sortOrders.Add(reader.GetInt32(1) == 1 ? "DESC" : "ASC");
                collations.Add(NormalizeCollation(hasCollation ? reader.GetString(2) : null));
            }

            _ = indexes.Add(new IndexContract(
                source == SqliteIndexSource.Explicit ? NormalizeIdentifier(name) : null,
                NormalizeIdentifiers(columns),
                NormalizeIdentifiers(sortOrders),
                NormalizeIdentifiers(collations),
                isUnique,
                filter,
                source));
        }

        return indexes;
    }

    private static async Task<string?> ReadSqliteIndexFilterAsync(
        DbConnection connection,
        string indexName,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT sql FROM sqlite_schema WHERE type = 'index' AND name = @indexName";
        AddParameter(command, "@indexName", indexName);
        object? sqlValue = await command.ExecuteScalarAsync(cancellationToken);
        if (sqlValue is not string sql)
        {
            return null;
        }

        int whereIndex = sql.IndexOf(" WHERE ", StringComparison.OrdinalIgnoreCase);
        return whereIndex < 0 ? null : NormalizeSql(sql[(whereIndex + 7)..]);
    }

    private static async Task<HashSet<ForeignKeyContract>> ReadSqliteForeignKeysAsync(
        DbConnection connection,
        TableIdentifier table,
        CancellationToken cancellationToken)
    {
        var foreignKeys = new Dictionary<long, SqliteForeignKeyBuilder>();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, seq, \"table\", \"from\", \"to\", on_update, on_delete " +
            "FROM pragma_foreign_key_list(@tableName) ORDER BY id, seq";
        AddParameter(command, "@tableName", table.Name);
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            long id = reader.GetInt64(0);
            if (!foreignKeys.TryGetValue(id, out SqliteForeignKeyBuilder? foreignKey))
            {
                foreignKey = new SqliteForeignKeyBuilder(
                    reader.GetString(2),
                    reader.GetString(5),
                    reader.GetString(6));
                foreignKeys.Add(id, foreignKey);
            }

            foreignKey.Columns.Add(reader.GetString(3));
            foreignKey.PrincipalColumns.Add(reader.GetString(4));
        }

        return
        [
            .. foreignKeys.Values.Select(foreignKey => new ForeignKeyContract(
                NormalizeIdentifier(foreignKey.PrincipalTable),
                NormalizeIdentifiers(foreignKey.Columns),
                NormalizeIdentifiers(foreignKey.PrincipalColumns),
                NormalizeSql(foreignKey.OnUpdateAction) ?? string.Empty,
                NormalizeSql(foreignKey.OnDeleteAction) ?? string.Empty)),
        ];
    }

    private static async Task<string?> ReadSqliteCreateTableSqlAsync(
        DbConnection connection,
        TableIdentifier table,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT sql FROM sqlite_schema WHERE type = 'table' AND name = @tableName";
        AddParameter(command, "@tableName", table.Name);
        object? sqlValue = await command.ExecuteScalarAsync(cancellationToken);
        return sqlValue as string;
    }

    private static HashSet<CheckConstraintContract> ExtractSqliteCheckConstraints(
        string createTableSql)
    {
        createTableSql = RemoveSqlComments(createTableSql);
        var constraints = new HashSet<CheckConstraintContract>();
        int index = 0;
        while (index <= createTableSql.Length - 5)
        {
            if (!createTableSql.AsSpan(index, 5).Equals("CHECK", StringComparison.OrdinalIgnoreCase) ||
                (index > 0 && IsSqlIdentifierCharacter(createTableSql[index - 1])) ||
                (index + 5 < createTableSql.Length &&
                 IsSqlIdentifierCharacter(createTableSql[index + 5])))
            {
                index++;
                continue;
            }

            int openingParenthesis = index + 5;
            while (openingParenthesis < createTableSql.Length &&
                   char.IsWhiteSpace(createTableSql[openingParenthesis]))
            {
                openingParenthesis++;
            }

            if (openingParenthesis >= createTableSql.Length ||
                createTableSql[openingParenthesis] != '(')
            {
                index++;
                continue;
            }

            int closingParenthesis = FindSqlClosingParenthesis(
                createTableSql,
                openingParenthesis);
            if (closingParenthesis < 0)
            {
                index++;
                continue;
            }

            string expression = createTableSql[
                (openingParenthesis + 1)..closingParenthesis];
            _ = constraints.Add(
                new CheckConstraintContract(NormalizeSql(expression) ?? string.Empty));
            index = closingParenthesis + 1;
        }

        return constraints;
    }

    private static string RemoveSqlComments(string sql)
    {
        var result = new StringBuilder(sql.Length);
        char closingQuote = '\0';
        int index = 0;
        while (index < sql.Length)
        {
            char character = sql[index];
            if (closingQuote != '\0')
            {
                _ = result.Append(character);
                if (character != closingQuote)
                {
                    index++;
                    continue;
                }

                if (index + 1 < sql.Length && sql[index + 1] == closingQuote)
                {
                    _ = result.Append(sql[index + 1]);
                    index += 2;
                    continue;
                }

                closingQuote = '\0';
                index++;
                continue;
            }

            if (character == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                _ = result.Append(' ');
                index += 2;
                while (index < sql.Length && sql[index] is not '\r' and not '\n')
                {
                    index++;
                }

                continue;
            }

            if (character == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                _ = result.Append(' ');
                index += 2;
                while (index + 1 < sql.Length &&
                       (sql[index] != '*' || sql[index + 1] != '/'))
                {
                    index++;
                }

                index = Math.Min(index + 2, sql.Length);
                continue;
            }

            closingQuote = character switch
            {
                '\'' => '\'',
                '"' => '"',
                '`' => '`',
                '[' => ']',
                _ => '\0',
            };
            _ = result.Append(character);
            index++;
        }

        return result.ToString();
    }

    private static bool ContainsSqlKeyword(string sql, string keyword)
    {
        sql = RemoveSqlComments(sql);
        char closingQuote = '\0';
        int index = 0;
        while (index < sql.Length)
        {
            char character = sql[index];
            if (closingQuote != '\0')
            {
                if (character != closingQuote)
                {
                    index++;
                    continue;
                }

                if (index + 1 < sql.Length && sql[index + 1] == closingQuote)
                {
                    index += 2;
                    continue;
                }

                closingQuote = '\0';
                index++;
                continue;
            }

            closingQuote = character switch
            {
                '\'' => '\'',
                '"' => '"',
                '`' => '`',
                '[' => ']',
                _ => '\0',
            };
            if (closingQuote != '\0')
            {
                index++;
                continue;
            }

            if (index <= sql.Length - keyword.Length &&
                sql.AsSpan(index, keyword.Length).Equals(
                    keyword,
                    StringComparison.OrdinalIgnoreCase) &&
                (index == 0 || !IsSqlIdentifierCharacter(sql[index - 1])) &&
                (index + keyword.Length == sql.Length ||
                 !IsSqlIdentifierCharacter(sql[index + keyword.Length])))
            {
                return true;
            }

            index++;
        }

        return false;
    }

    private static int FindSqlClosingParenthesis(string sql, int openingParenthesis)
    {
        int depth = 0;
        char closingQuote = '\0';
        int index = openingParenthesis;
        while (index < sql.Length)
        {
            char character = sql[index];
            if (closingQuote != '\0')
            {
                if (character != closingQuote)
                {
                    index++;
                    continue;
                }

                if (index + 1 < sql.Length && sql[index + 1] == closingQuote)
                {
                    index += 2;
                    continue;
                }

                closingQuote = '\0';
                index++;
                continue;
            }

            closingQuote = character switch
            {
                '\'' => '\'',
                '"' => '"',
                '`' => '`',
                '[' => ']',
                _ => '\0',
            };
            if (closingQuote != '\0')
            {
                index++;
                continue;
            }

            if (character == '(')
            {
                depth++;
            }
            else if (character == ')' && --depth == 0)
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    private static bool IsSqlIdentifierCharacter(char character) =>
        char.IsLetterOrDigit(character) || character == '_';

    private static void ValidateSqliteTable(
        TableIdentifier table,
        TableContract expected,
        SqliteTableContract actual,
        List<string> mismatches)
    {
        foreach ((string columnName, ColumnContract expectedColumn) in expected.Columns)
        {
            if (!actual.Columns.TryGetValue(columnName, out ColumnContract? actualColumn))
            {
                mismatches.Add($"{table.DisplayName}.{columnName}");
                continue;
            }

            if (!string.Equals(
                    NormalizeStoreType(expectedColumn.StoreType),
                    NormalizeStoreType(actualColumn.StoreType),
                    StringComparison.Ordinal))
            {
                mismatches.Add($"{table.DisplayName}.{columnName} (store type)");
            }

            if (expectedColumn.IsNullable != actualColumn.IsNullable)
            {
                mismatches.Add($"{table.DisplayName}.{columnName} (nullability)");
            }

            if (!string.Equals(
                    expectedColumn.DefaultSql,
                    actualColumn.DefaultSql,
                    StringComparison.Ordinal))
            {
                mismatches.Add($"{table.DisplayName}.{columnName} (default)");
            }

            if (expectedColumn.PrimaryKeyOrdinal != actualColumn.PrimaryKeyOrdinal)
            {
                mismatches.Add($"{table.DisplayName}.{columnName} (primary key)");
            }

            if (expectedColumn.IsAutoIncrement != actualColumn.IsAutoIncrement)
            {
                mismatches.Add($"{table.DisplayName}.{columnName} (autoincrement)");
            }
        }

        mismatches.AddRange(
            actual.Columns.Keys
                .Where(column => !expected.Columns.ContainsKey(column))
                .Select(column => $"{table.DisplayName}.{column} (unexpected column)"));
        mismatches.AddRange(
            expected.Indexes
                .Where(index => !actual.Indexes.Contains(index))
                .Select(index => DescribeIndex(table, index, unexpected: false)));
        mismatches.AddRange(
            actual.Indexes
                .Where(index => !expected.Indexes.Contains(index))
                .Select(index => DescribeIndex(table, index, unexpected: true)));
        mismatches.AddRange(
            expected.ForeignKeys
                .Where(foreignKey => !actual.ForeignKeys.Contains(foreignKey))
                .Select(foreignKey =>
                    $"{table.DisplayName} (foreign key: {foreignKey.Columns} -> " +
                    $"{foreignKey.PrincipalTable}.{foreignKey.PrincipalColumns})"));
        mismatches.AddRange(
            actual.ForeignKeys
                .Where(foreignKey => !expected.ForeignKeys.Contains(foreignKey))
                .Select(foreignKey =>
                    $"{table.DisplayName} (unexpected foreign key: {foreignKey.Columns} -> " +
                    $"{foreignKey.PrincipalTable}.{foreignKey.PrincipalColumns})"));
        mismatches.AddRange(
            expected.CheckConstraints
                .Where(checkConstraint => !actual.CheckConstraints.Contains(checkConstraint))
                .Select(checkConstraint =>
                    $"{table.DisplayName} (check constraint: {checkConstraint.Sql})"));
        mismatches.AddRange(
            actual.CheckConstraints
                .Where(checkConstraint => !expected.CheckConstraints.Contains(checkConstraint))
                .Select(checkConstraint =>
                    $"{table.DisplayName} (unexpected check constraint: {checkConstraint.Sql})"));
    }

    private static string? GetDefaultSql(IColumn column)
    {
        if (!string.IsNullOrWhiteSpace(column.DefaultValueSql))
        {
            return NormalizeSql(column.DefaultValueSql);
        }

        return column.DefaultValue is null or DBNull
            ? null
            : NormalizeSql(column.StoreTypeMapping.GenerateSqlLiteral(column.DefaultValue));
    }

    private static string NormalizeIdentifier(string identifier) =>
        identifier.Trim().ToUpperInvariant();

    private static string NormalizeIdentifiers(IEnumerable<string> identifiers) =>
        string.Join("|", identifiers.Select(NormalizeIdentifier));

    private static string NormalizeIndexSortOrders(
        int columnCount,
        IReadOnlyList<bool>? isDescending) =>
        string.Join(
            "|",
            Enumerable.Range(0, columnCount).Select(index =>
                isDescending is not null &&
                (isDescending.Count == 0 || isDescending[index])
                    ? "DESC"
                    : "ASC"));

    private static string NormalizeIndexCollations(
        IEnumerable<IColumn> columns,
        StoreObjectIdentifier storeObject,
        string defaultCollation) =>
        NormalizeIdentifiers(columns.Select(column =>
            column.PropertyMappings
                .Select(mapping => mapping.Property.GetCollation(storeObject))
                .FirstOrDefault(collation => !string.IsNullOrWhiteSpace(collation))
            ?? defaultCollation));

    private static string NormalizeCollation(string? collation) =>
        NormalizeIdentifier(string.IsNullOrWhiteSpace(collation) ? "BINARY" : collation);

    private static string DescribeIndex(
        TableIdentifier table,
        IndexContract index,
        bool unexpected)
    {
        string indexType = index.IsUnique ? "unique index" : "index";
        string prefix = unexpected ? $"unexpected {indexType}" : indexType;
        string name = index.Name ?? "<sqlite-autoindex>";
        string source = index.Source switch
        {
            SqliteIndexSource.PrimaryKey => "primary key",
            SqliteIndexSource.UniqueConstraint => "unique constraint",
            _ => "explicit",
        };
        return $"{table.DisplayName} ({prefix}: {index.Columns}) " +
               $"(name: {name}; sort: {index.SortOrders}; collation: {index.Collations}; source: {source})";
    }

    private static string NormalizeStoreType(string storeType) =>
        string.Concat(storeType.Where(character => !char.IsWhiteSpace(character)))
            .ToUpperInvariant();

    private static string? NormalizeSql(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return null;
        }

        string normalized = sql.Trim();
        while (normalized.Length >= 2 &&
               normalized[0] == '(' &&
               normalized[^1] == ')')
        {
            normalized = normalized[1..^1].Trim();
        }

        return normalized;
    }

    private static string NormalizeReferentialAction(ReferentialAction action) =>
        action switch
        {
            ReferentialAction.Cascade => "CASCADE",
            ReferentialAction.Restrict => "RESTRICT",
            ReferentialAction.SetNull => "SET NULL",
            ReferentialAction.SetDefault => "SET DEFAULT",
            _ => "NO ACTION",
        };

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

    private enum SqliteIndexSource
    {
        Explicit,
        UniqueConstraint,
        PrimaryKey,
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

    private sealed class SchemaContract(Dictionary<TableIdentifier, TableContract> tables)
    {
        public IReadOnlyDictionary<TableIdentifier, TableContract> Tables { get; } = tables;

        public TableIdentifier Find(string tableName) =>
            Tables.Keys.FirstOrDefault(table =>
                string.Equals(table.Name, tableName, StringComparison.OrdinalIgnoreCase))
            ?? new TableIdentifier(tableName, null);
    }

    private sealed record ColumnContract(
        string StoreType,
        bool IsNullable,
        string? DefaultSql,
        int PrimaryKeyOrdinal,
        bool IsAutoIncrement);

    private sealed record IndexContract(
        string? Name,
        string Columns,
        string SortOrders,
        string Collations,
        bool IsUnique,
        string? Filter,
        SqliteIndexSource Source);

    private sealed record ForeignKeyContract(
        string PrincipalTable,
        string Columns,
        string PrincipalColumns,
        string OnUpdateAction,
        string OnDeleteAction);

    private sealed record CheckConstraintContract(string Sql);

    private sealed record TableContract(
        IReadOnlyDictionary<string, ColumnContract> Columns,
        IReadOnlySet<IndexContract> Indexes,
        IReadOnlySet<ForeignKeyContract> ForeignKeys,
        IReadOnlySet<CheckConstraintContract> CheckConstraints);

    private sealed record SqliteTableContract(
        IReadOnlyDictionary<string, ColumnContract> Columns,
        IReadOnlySet<IndexContract> Indexes,
        IReadOnlySet<ForeignKeyContract> ForeignKeys,
        IReadOnlySet<CheckConstraintContract> CheckConstraints);

    private sealed class SqliteForeignKeyBuilder(
        string principalTable,
        string onUpdateAction,
        string onDeleteAction)
    {
        public string PrincipalTable { get; } = principalTable;

        public string OnUpdateAction { get; } = onUpdateAction;

        public string OnDeleteAction { get; } = onDeleteAction;

        public List<string> Columns { get; } = [];

        public List<string> PrincipalColumns { get; } = [];
    }
}
