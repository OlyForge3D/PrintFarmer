using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// Proves that a database context's schema has never been migrated on this host (issue #3126).
/// A fresh host has no tables yet, so a host-update read of an absent table would fail. That
/// failure may be read as "no work" only when the context's own migration history table is also
/// absent: a missing table next to a present history table is a damaged or foreign schema, and a
/// read error is never a proof of anything. The proof is a positive catalog check, never an
/// interpretation of a failed query, and it is re-checked after the table probe so a migration
/// that started concurrently cannot be mistaken for a fresh host.
/// </summary>
public static class HostUpdateSchemaAbsenceProof
{
    internal const string DefaultHistoryTableName = "__EFMigrationsHistory";

    /// <summary>
    /// True only when <paramref name="context"/>'s migration history table and every table mapped
    /// to <paramref name="entityTypes"/> are provably absent. Any catalog read failure propagates.
    /// </summary>
    public static async Task<bool> IsNeverMigratedAsync(DbContext context, IReadOnlyList<Type> entityTypes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entityTypes);
        if (entityTypes.Count == 0)
        {
            throw new ArgumentException("At least one entity type is required.", nameof(entityTypes));
        }

        List<(string? Schema, string Table)> tables = [];
        foreach (Type type in entityTypes)
        {
            IEntityType entity = context.Model.FindEntityType(type)
                ?? throw new InvalidOperationException("schema_absence_entity_unmapped:" + type.Name);
            tables.Add((entity.GetSchema(), entity.GetTableName() ?? throw new InvalidOperationException("schema_absence_table_unmapped:" + type.Name)));
        }

        IHistoryRepository history = context.GetService<IHistoryRepository>();
        if (await history.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        DbConnection connection = context.Database.GetDbConnection();
        DbTransaction? transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach ((string? schema, string table) in tables)
            {
                if (await TableExistsAsync(connection, transaction, schema, table, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        return !await history.ExistsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Raw ADO.NET form for callers that deliberately run no EF model (the read-only manifest
    /// binding reader). The history table is the default-schema <c>__EFMigrationsHistory</c> of the
    /// main application context. Any catalog read failure propagates.
    /// </summary>
    public static async Task<bool> IsNeverMigratedAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string? historySchema,
        IReadOnlyList<(string? Schema, string Table)> tables,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tables);
        if (tables.Count == 0)
        {
            throw new ArgumentException("At least one table is required.", nameof(tables));
        }

        if (await TableExistsAsync(connection, transaction, historySchema, DefaultHistoryTableName, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        foreach ((string? schema, string table) in tables)
        {
            if (await TableExistsAsync(connection, transaction, schema, table, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return !await TableExistsAsync(connection, transaction, historySchema, DefaultHistoryTableName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Catalog probe for one table. An unqualified name resolves the way an unqualified query does:
    /// PostgreSQL's <c>current_schema()</c>, SQL Server's default schema, and SQLite's main schema.
    /// </summary>
    internal static async Task<bool> TableExistsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string? schema,
        string table,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 15;
        switch (connection)
        {
            case NpgsqlConnection:
                command.CommandText =
                    "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace " +
                    "WHERE c.relname = @table AND n.nspname = COALESCE(@schema, current_schema()) AND c.relkind IN ('r', 'p'))";
                AddParameter(command, "@table", table);
                AddParameter(command, "@schema", schema);
                break;
            case SqlConnection:
                command.CommandText = "SELECT CASE WHEN OBJECT_ID(@name, N'U') IS NULL THEN 0 ELSE 1 END";
                AddParameter(command, "@name", schema is null ? Bracket(table) : Bracket(schema) + "." + Bracket(table));
                break;
            case SqliteConnection:
                // SQLite has no schemas; every context maps into the main database.
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table";
                AddParameter(command, "@table", table);
                break;
            default:
                throw new NotSupportedException("database_provider_unsupported");
        }

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value switch
        {
            bool exists => exists,
            null or DBNull => throw new InvalidDataException("schema_absence_probe_unreadable"),
            _ => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) != 0,
        };
    }

    private static void AddParameter(DbCommand command, string name, string? value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = System.Data.DbType.String;
        parameter.Value = (object?)value ?? DBNull.Value;
        _ = command.Parameters.Add(parameter);
    }

    private static string Bracket(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
